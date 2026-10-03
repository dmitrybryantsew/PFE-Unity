namespace PFE.Systems.Weapons
{
    /// <summary>
    /// The player-side multipliers AS3 copies out of <c>Pers</c> onto the <b>weapon instance</b>
    /// in <c>Weapon.setParams</c> (<c>Weapon.as:973-977</c>), and which the weapon then reads from
    /// its own fields.
    ///
    /// <para><b>Why an interface rather than passing <c>CharacterStats</c>.</b> The weapon
    /// controllers are plain classes owned by <c>PlayerWeaponLoadout</c>, and enemies will use the
    /// same factory. Taking a narrow interface keeps the weapon side from depending on the RPG
    /// model, and lets a test supply fixed multipliers without building a <c>CharacterStats</c>.
    /// The same reasoning produced <c>IAmmoSource</c> next door.</para>
    ///
    /// <para><b>Null means "no owner stats", not "defaults".</b> Every consumer must tolerate null
    /// and fall back to the AS3 <c>Pers</c> declaration defaults, which are what the values are
    /// before any <c>&lt;sk&gt;</c> applies: <c>reloadMult</c> 1, <c>recoilMult</c> 1,
    /// <c>jammedMult</c> 1, <c>recyc</c> 0. Note <c>recyc</c> defaults to <b>0</b>, i.e. "never
    /// recycle" — a null source must not start refunding ammunition.</para>
    /// </summary>
    public interface IWeaponStatSource
    {
        /// <summary>
        /// <c>Pers.reloadMult</c> (<c>Pers.as:187</c>) — scales reload duration:
        /// <c>t_reload = round(reload * reloadMult)</c> (<c>Weapon.as:1939</c>), and the completion
        /// threshold <c>t_reload == round(10 * reloadMult)</c> (<c>:1272</c>).
        /// </summary>
        float ReloadMult { get; }

        /// <summary>
        /// <c>Pers.recoilMult</c> (<c>Pers.as:197</c>) — scales both recoil terms:
        /// <c>t_ret = round(recoil * recoilMult)</c> and <c>rotUp += recoilUp * recoilMult</c>
        /// (<c>Weapon.as:1612-1617</c>).
        /// </summary>
        float RecoilMult { get; }

        /// <summary>
        /// <c>Pers.jammedMult</c> (<c>Pers.as:251</c>) — scales the jam and misfire probabilities
        /// (<c>Weapon.as:1429</c>). Higher means <i>more</i> jams; the perk that grants it is a
        /// drawback.
        /// </summary>
        float JammedMult { get; }

        /// <summary>
        /// <c>Pers.recyc</c> (<c>Pers.as:201</c>) — the probability that firing does <b>not</b>
        /// consume a round, for <c>batt</c>/<c>energ</c>/<c>crystal</c> ammo only
        /// (<c>Weapon.as:1586</c>). A 0..1 chance, and 0 by default.
        /// </summary>
        float Recyc { get; }

        // ── Melee / unarmed multipliers ────────────────────────────────────────
        //
        // Four of these are copied onto the weapon instance by the melee override of setPers
        // (WClub.as:204-205) and read by its resultDamage/resultRapid overrides (WClub.as:649,658).
        // They are NOT read by the base Weapon, so a ranged weapon ignores them entirely.
        //
        // The fifth, MeleeRun, is read straight off Pers inside WClub.actions() rather than being
        // copied, because it is a levitation cost, not a weapon parameter.

        /// <summary>
        /// <c>Pers.meleeDamMult</c> (<c>Pers.as:179</c>, 1) — melee damage multiplier, applied as
        /// <c>damMult *= meleeDamMult</c> (<c>WClub.as:205</c>) and read in the melee
        /// <c>resultDamage</c> (<c>WClub.as:649</c>). Ranged weapons do not see it.
        /// </summary>
        float MeleeDamMult { get; }

        /// <summary>
        /// <c>Pers.meleeSpdMult</c> (<c>Pers.as:177</c>, 1) — melee <b>attack speed</b> multiplier.
        /// Applied inverted, as a swing-duration divisor:
        /// <c>rapidMult = 1 / meleeSpdMult</c> (<c>WClub.as:204</c>), then
        /// <c>resultRapid = rapid / skillConf * rapidMult / owner.rapidMultCont</c>
        /// (<c>WClub.as:658</c>). Higher = <i>faster</i> swings; do not multiply a duration by it.
        /// </summary>
        float MeleeSpdMult { get; }

        /// <summary>
        /// <c>Pers.punchDamMult</c> (<c>Pers.as:333</c>, 1) — unarmed damage multiplier. In
        /// <c>WKick.as</c> it scales three separate things: <c>b.damage</c> and <c>b.otbros</c>
        /// (<c>:47-48</c>), and, when it is above 1, the stun proc chance
        /// <c>dopCh = punchDamMult - 1</c> (<c>:52-54</c>).
        /// </summary>
        float PunchDamMult { get; }

        /// <summary>
        /// <c>Pers.kickDestroy</c> (<c>Pers.as:335</c>, 30) — the tile/structure destruction budget
        /// a <b>kick</b> carries (<c>WKick.as:69</c>). A punch leaves the weapon's own
        /// <c>destroy</c> value alone; only the kick branch overrides it. Flat, not a multiplier.
        /// </summary>
        float KickDestroy { get; }

        /// <summary>
        /// <c>Pers.meleeRun</c> (<c>Pers.as:175</c>, <b>10</b>) — the melee <i>levitation-run</i>
        /// distance, quadrupled in the sky (<c>WClub.as:249-252</c>). Read only on the player branch.
        ///
        /// <para><b>Not a defaultParams() field.</b> Like <c>jammedMult</c> and <c>recyc</c>,
        /// <c>Pers.as:820-960</c> never assigns it, so a perk that raises it leaves the value behind
        /// when removed. It <i>is</i> written by the melee skill (<c>AllData.as:5227</c>:
        /// <c>v0='10' vd='3'</c>).</para>
        /// </summary>
        float MeleeRun { get; }
    }
}
