namespace PFE.Systems.Combat
{
    /// <summary>
    /// AS3 <c>UnitDamager</c> (<c>fe/unit/UnitDamager.as</c>) — the trap family that is <i>dormant</i>
    /// until a script wakes it: <c>damshot</c> (a hidden shooter), <c>damgren</c> (a hidden grenade
    /// thrower) and <c>damexpl1</c> (a satchel charge).
    ///
    /// <para><b>Why the rules are here and not in the brain.</b> The three tips differ in exactly two
    /// places — what <c>activate()</c> does, and whether there is a weapon at all — and every number in
    /// the bomb arm is a literal in the oracle (<c>250</c>/<c>1000</c>/<c>200</c>, plus the
    /// <c>Y - 20</c> the blast is anchored at). Those are the kind of values that get "tidied" into a
    /// definition field and silently stop matching the game, so they are constants with citations and a
    /// fixture asserts them. The brain keeps only the wiring.</para>
    /// </summary>
    public static class DamagerTrapRules
    {
        /// <summary>A hidden shooter: <c>Weapon.create</c> from <c>tr</c>, then <c>currentWeapon.attack()</c>.</summary>
        public const int TipShooter = 1;

        /// <summary>A hidden grenade thrower: <c>hgren</c>, <c>och = 3</c>, arcs at the target.</summary>
        public const int TipThrower = 2;

        /// <summary>A satchel charge: no weapon, one explosion, then the unit removes itself.</summary>
        public const int TipBomb = 3;

        /// <summary>
        /// AS3 <c>internal var tipDamager:int = 1</c> (<c>UnitDamager.as:18</c>) — the value an
        /// <c>&lt;un&gt;</c> node that authors no <c>tip</c> leaves in place. Written here so
        /// <c>UnitDefinition.damagerTip</c> can default to the oracle's own number rather than to zero.
        /// </summary>
        public const int DefaultTip = TipShooter;

        /// <summary><c>this.damageExpl = 250</c> — <c>UnitDamager.as:152</c>, tip 3 arm of <c>setWeapon</c>.</summary>
        public const float BombDamage = 250f;

        /// <summary><c>this.destroyExpl = 1000</c> — <c>UnitDamager.as:153</c>. Tile hp per hit, not unit damage.</summary>
        public const float BombDestroyTiles = 1000f;

        /// <summary><c>this.explRadius = 200</c> — <c>UnitDamager.as:154</c>, in AS3 pixels.</summary>
        public const float BombRadiusPixels = 200f;

        /// <summary>
        /// <c>new Bullet(this, X, Y - 20, null, false)</c> — <c>UnitDamager.as:349</c>, the arm taken when
        /// <c>currentWeapon</c> is null (a tip-3 damager never builds one).
        ///
        /// <para><b>The sign is the mirror, not a copy.</b> AS3's Y grows <i>down</i>, so <c>Y - 20</c> is
        /// twenty pixels <b>above</b> the unit's feet. The port's Y grows up, so the same point is
        /// <c>+20</c>. Copying the minus sign would put every blast under the floor.</para>
        /// </summary>
        public const float BombCentreOffsetPixels = 20f;

        /// <summary>
        /// <c>internal function activate()</c> (<c>UnitDamager.as:354-372</c>) opens with
        /// <c>if(this.status != 0 || sost &gt; 1) return;</c> — a damager fires once and a dead one never.
        /// <c>status</c> 0 is the only armed-but-dormant state; 1 means it is already shooting.
        /// </summary>
        public static bool CanActivate(int status) => status == 0;

        /// <summary>
        /// <c>UnitDamager.control()</c>'s only attack gate: <c>if(this.status == 1)</c>
        /// (<c>UnitDamager.as:407</c>). A damager that was never commanded does <b>not</b> shoot — which
        /// is the whole reason it is invisible until a script wakes it.
        /// </summary>
        public static bool Attacks(int status) => status == 1;

        /// <summary>The <c>tipDamager == 3</c> arm: no weapon, one blast, then <c>disarm()</c>.</summary>
        public static bool IsBomb(int tip) => tip == TipBomb;

        /// <summary>Pixels to world units for the blast geometry (1 unit = 100 px — <c>TileQueryConstants</c>).</summary>
        public const float PixelToUnit = 0.01f;
    }
}
