namespace PFE.Sim.Combat.Sats
{
    /// <summary>
    /// AS3 <c>Weapon.status()</c> (<c>Weapon.as:1827-1858</c>) — the seven-state summary the rest of
    /// the weapon code branches on, reduced to a pure function of flags.
    ///
    /// <para><b>Why it is a separate pure file.</b> S.A.T.S. reads this number in three different
    /// places with three different consequences: entry refuses on 4/5/6 with a <i>different message
    /// each</i> (<c>Sats.as:104-118</c>), <c>act()</c> pops a cell early when it is above 1
    /// (<c>Sats.as:311</c>), and the queue's own invalidation clears everything above 3
    /// (<c>UnitPlayer.as:1152-1155</c>). Getting the precedence wrong is invisible — every wrong
    /// ordering still returns <i>a</i> status — so the ordering is pinned by a fixture rather than
    /// left inside a controller that needs a player, a weapon and a room to run.</para>
    ///
    /// <para><b>The precedence is the Oracle's, top to bottom:</b> broken beats jammed beats an empty
    /// magazine beats mana beats the attack timer beats the reload timer. Two of those are worth
    /// stating: a broken weapon reports 5 even while reloading (so S.A.T.S. says "broken", not
    /// "busy"), and a weapon that is both jammed and out of reserve ammo reports 2 (jammed) because
    /// the magazine test runs first and the reserve test only decides <i>which</i> 2-adjacent answer
    /// to give.</para>
    /// </summary>
    public static class SatsWeaponStatus
    {
        /// <summary>Ready to fire — <c>Weapon.as:1857</c>'s fall-through.</summary>
        public const int Ready = 0;

        /// <summary>Mid-attack; the fire timer has not cleared.</summary>
        public const int Attacking = 1;

        /// <summary>Jammed, or an empty magazine with rounds in reserve (a reload is owed).</summary>
        public const int Jammed = 2;

        /// <summary>Reloading.</summary>
        public const int Reloading = 3;

        /// <summary>No round in the magazine and none in reserve — S.A.T.S. says <c>noAmmo</c>.</summary>
        public const int NoAmmo = 4;

        /// <summary>Condition has reached zero — S.A.T.S. says <c>brokenWeapon</c>.</summary>
        public const int Broken = 5;

        /// <summary>A magic weapon whose next cast costs more mana than the caster has.</summary>
        public const int NoMana = 6;

        /// <summary>
        /// <c>Weapon.status()</c>, as a function of the flags the port already tracks.
        ///
        /// <para><b>The mana clause carries an extra condition that is easy to drop.</b> AS3 reads
        /// <c>if (dmagic &gt; owner.mana &amp;&amp; owner.mana &lt; owner.maxmana * 0.99)</c> — the
        /// second half is <c>Pers.CanAffordMagic</c>'s own exception, so a caster at full mana may
        /// cast a spell that costs more than the pool. Without it a full-mana caster is refused the
        /// one spell they can always pay for.</para>
        /// </summary>
        /// <param name="broken">AS3 <c>hp &lt;= 0</c> — <c>WeaponRuntimeState.IsBroken</c>.</param>
        /// <param name="jammed">AS3 <c>jammed</c> — <c>WeaponRuntimeState.Jammed</c>.</param>
        /// <param name="magazineEmpty">AS3 <c>hold &lt; rashod</c> — the next shot has no round.</param>
        /// <param name="reserveEmpty">AS3 <c>invent.items[ammo].kol &lt; rashod</c> — nothing to reload from.</param>
        /// <param name="manaShort">AS3 <c>dmagic &gt; owner.mana</c> — this cast is unaffordable.</param>
        /// <param name="manaAtFull">AS3 <c>owner.mana &lt; owner.maxmana * 0.99</c> is <b>false</b>,
        ///   i.e. the caster is at (or within 1 % of) full mana.</param>
        /// <param name="attacking">AS3 <c>t_attack &gt; 0</c>.</param>
        /// <param name="reloading">AS3 <c>t_reload &gt; 0</c>.</param>
        /// <param name="consumesMagazine">AS3's <c>ammo != "recharg" &amp;&amp; ammo != "not" &amp;&amp; holder &gt; 0</c>
        ///   — a self-recharging or ammo-less weapon never reports an empty magazine.</param>
        public static int Compute(
            bool broken, bool jammed, bool magazineEmpty, bool reserveEmpty,
            bool manaShort, bool manaAtFull, bool attacking, bool reloading,
            bool consumesMagazine)
        {
            if (broken) return Broken;
            if (jammed) return Jammed;

            if (consumesMagazine && magazineEmpty)
            {
                return reserveEmpty ? NoAmmo : Jammed;
            }

            if (manaShort && !manaAtFull) return NoMana;

            if (attacking) return Attacking;
            if (reloading) return Reloading;

            return Ready;
        }

        /// <summary>
        /// Whether S.A.T.S. may open at all — the four status codes that refuse entry
        /// (<c>Sats.as:104-118</c>).
        ///
        /// <para>Note <c>Attacking</c> and <c>Reloading</c> are <b>not</b> refusals: a player may enter
        /// mid-reload, and the mode waits. Only 4, 5 and 6 block, and the Oracle tests them with
        /// separate <c>if</c>s rather than an <c>else if</c> chain — harmless there because only one
        /// can be true, and kept as a set here so the caller cannot depend on the order.</para>
        /// </summary>
        public static bool RefusesEntry(int status)
        {
            return status == NoAmmo || status == Broken || status == NoMana;
        }
    }
}
