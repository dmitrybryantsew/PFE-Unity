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

        // ── Attacker-side hit procs ───────────────────────────────────────────
        //
        // These two are NOT weapon parameters and are NOT copied in setParams like the multipliers
        // above. AS3 stamps them onto the *bullet* at fire time, reading the owner directly for one
        // and the weapon's cached copy for the other:
        //
        //   critInvis  Weapon.as:1697  `param1.critInvis = this.owner.critInvis;`
        //   desintegr  Weapon.as:1525-1527  `if(this.desintegr) this.b.desintegr = this.desintegr;`
        //              where Weapon.as:967-970  `if(param2.desintegr > 0) this.desintegr = param2.desintegr;`
        //
        // They sit here anyway because this interface is already "the values AS3 reads off Pers for a
        // shot", and because the alternative — a second source object on every controller — would be
        // one seam too many. The context carries them the rest of the way.

        /// <summary>
        /// <c>Unit.critInvis</c> (<c>Unit.as:322</c>, 0) — the stealth-crit probability, granted by
        /// the sneak skill. Read straight off the owner at <c>Weapon.as:1697</c>.
        ///
        /// <para><b>0, not a fallback, is the meaning of a null source.</b> A unit with no
        /// <c>Pers</c> has no stealth-crit chance, and 0 disables the second crit roll entirely —
        /// consuming no random number, which is what keeps the combat stream in step.</para>
        /// </summary>
        float CritInvis { get; }

        /// <summary>
        /// <c>Pers.desintegr</c> (<c>Pers.as:199</c>, 0) — the disintegration (overkill) probability,
        /// granted by the level-15 <c>desintegr</c> perk. Copied to the weapon at
        /// <c>Weapon.as:967-970</c> and then to the bullet at <c>:1525-1527</c>.
        ///
        /// <para><b>0 disables the proc.</b> AS3's copy is itself gated on
        /// <c>param2.desintegr &gt; 0</c>, so a null source (0 here) reproduces the oracle's "no perk,
        /// no disintegr" state exactly.</para>
        /// </summary>
        float Desintegr { get; }

        // ── The precision channel ─────────────────────────────────────────────
        //
        // AS3 builds the shot's precision in TWO places, and both must move together:
        //
        //  1. `Weapon.setParams` copies the owner's standing multiplier onto the weapon:
        //         Weapon.as:976   this.precMult = param2.allPrecMult;
        //         Weapon.as:1006  this.precMult *= param2[perk + "Prec"];   // per-perk, e.g. pistolPrec
        //  2. `UnitPlayer.control()` rebuilds the *situational* multiplier every tick from the
        //     player's locomotion state (UnitPlayer.as:1181-1200) and copies it to `owner.precMult`:
        //
        //         precMult = pers.allPrecMult;
        //         if(sats.que.length == 0 && !lurked) {
        //            if(pers.runPenalty > 0 && (|dx|>10 || |dy|>10)) precMult *= 1 - pers.runPenalty;
        //            if(!stay)                                      precMult *= 1 - pers.jumpPenalty;
        //            if(stay && |dx| < 1)                           precMult *= 1 + pers.stayBonus;
        //            if(currentWeapon && currentWeapon.storona != storona)
        //                                                           precMult *= 1 - pers.backPenalty;
        //         }
        //
        //  and the bullet is stamped with the *composition*:
        //         Weapon.as:1634  resultPrec(p1,p2) = precision * precMult * (1 + (p2-1)*0.5)
        //                                            * p1 * owner.precMultCont;
        //         Weapon.as:1531  this.b.precision = this.resultPrec(this.owner.precMult, _loc1_);
        //
        //  So `b.precision` is the ROUND's precision in pixels (data value * 40), already carrying
        //  the owner's multipliers — which is why the port can fold them into DamageContext.Precision
        //  and let the existing HitAvoidance reader take the value unchanged.
        //
        //  `precMultCont` (Unit.as:328, reset by Pers.defaultParams:886) is a *status* multiplier —
        //  blindness 0.4, contusion 0.8, drunk 0.9/0.8/0.5/0.2, weak 0.5 (AllData.as:5928..6007).
        //  It is folded into the same PrecisionMultiplier term; note it is read off `owner`
        //  (UnitPlayer) and NOT off Pers, whereas `allPrecMult` is read off Pers. Both are 1 at rest.

        /// <summary>
        /// <c>Pers.allPrecMult</c> (<c>Pers.as:369</c>, 1) — the owner's standing precision
        /// multiplier before any locomotion state. Read twice in AS3: onto the weapon in
        /// <c>setParams</c> (<c>Weapon.as:976</c>) and as the seed of the per-tick
        /// <c>precMult</c> (<c>UnitPlayer.as:1181</c>). Written by three perks
        /// (<c>AllData.as:5316</c> ×2, <c>:5862</c>) and two flat adds (<c>:4215</c>, <c>:6217</c>).
        /// </summary>
        float AllPrecMult { get; }

        /// <summary>
        /// <c>Pers.runPenalty</c> (<c>Pers.as:189</c>, <b>0.5</b>) — the accuracy penalty applied
        /// while running (<c>UnitPlayer.as:1185-1187</c>), as <c>precMult *= 1 - runPenalty</c>.
        /// Only applies when positive <b>and</b> the player is moving &gt; 10 px/tick on either axis.
        ///
        /// <para>The default is the <i>declaration</i> value, not a <c>defaultParams()</c> one; the
        /// <c>rungun</c> perk lowers it to 0.25.</para>
        /// </summary>
        float RunPenalty { get; }

        /// <summary>
        /// <c>Pers.jumpPenalty</c> (<c>Pers.as:191</c>, <b>0.3</b>) — the airborne accuracy penalty
        /// (<c>UnitPlayer.as:1189-1191</c>, gated on <c>!stay</c>). The <c>rungun</c> perk lowers it
        /// to 0.15.
        /// </summary>
        float JumpPenalty { get; }

        /// <summary>
        /// <c>Pers.backPenalty</c> (<c>Pers.as:193</c>, <b>0.4</b>) — the penalty for firing behind
        /// you, applied when the weapon's facing differs from the body's
        /// (<c>UnitPlayer.as:1197-1199</c>). The <c>composure</c> perk lowers it to 0.2.
        /// </summary>
        float BackPenalty { get; }

        /// <summary>
        /// <c>Pers.stayBonus</c> (<c>Pers.as:195</c>, <b>0.3</b>) — the accuracy <i>bonus</i> for
        /// standing still (<c>UnitPlayer.as:1193-1195</c>, <c>stay &amp;&amp; |dx| &lt; 1</c>), applied
        /// as <c>precMult *= 1 + stayBonus</c>. The <c>composure</c> perk <i>raises</i> it to 0.45.
        /// </summary>
        float StayBonus { get; }

        /// <summary>
        /// <c>Pers.mazilAdd</c> (<c>Pers.as:371</c>, 0) — the "miss radius" addend, in the spread
        /// model's own units (<c>Weapon.as:1460</c>, <c>WThrow.as:139</c>). <b>Not</b> part of
        /// <c>precMult</c>: AS3 adds it to the muzzle-angle divisor, a different mechanism. Carried
        /// so the value is live; the launch path does not roll an angle yet.
        /// </summary>
        float MazilAdd { get; }

        /// <summary>
        /// The <b>composed</b> situational precision multiplier — AS3's <c>owner.precMult</c> at the
        /// moment of the shot, i.e. <see cref="AllPrecMult"/> put through the four locomotion terms
        /// of <c>UnitPlayer.as:1181-1200</c>.
        ///
        /// <para>This is the value the shot carries into <c>DamageContext.PrecisionMultiplier</c>, and
        /// the five raw fields above exist so a test or a debug view can see <i>which</i> term
        /// applied. A source that computes nothing (a test double, an enemy) returns 1, which leaves
        /// the weapon's own precision untouched.</para>
        /// </summary>
        float PrecisionMultiplier { get; }
    }
}
