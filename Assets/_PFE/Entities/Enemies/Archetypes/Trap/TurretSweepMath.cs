using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// The turret mount's <b>barrel behaviour</b> — the watch sweep, the easing rate and the elevation
    /// clamp — transcribed from <c>fe/unit/UnitTurret.as</c>.
    ///
    /// <para><b>What the oracle actually does, and why it needs three separate numbers.</b> A turret in
    /// <c>aiState 1</c> is <i>watching</i>: <c>control()</c> walks an <c>aRot</c> table of pre-set angles,
    /// one step every <c>period</c> ticks (<c>:615-621</c>), and hands the current entry to the weapon as
    /// <c>forceRot</c>. <c>Weapon.actions()</c> then eases the barrel toward that value at
    /// <c>drot</c> radians per frame (<c>Weapon.as:1087-1145</c>) instead of snapping — and because
    /// <c>drot</c> is <c>watchDrot</c> (0.015 rad ≈ 0.86°/frame) the barrel <i>drifts</i> toward the next
    /// watch angle over roughly two seconds, which is the slow sweep the mount is animated for. Once the
    /// turret commits (<c>aiState 3</c>) <c>control()</c> sets <c>findCel = true</c> and <c>drot =
    /// actDrot</c> (0.1 rad ≈ 5.7°/frame), so the same barrel snaps onto the player in about a fifth of
    /// a second. Hiding mounts use 0.5.</para>
    ///
    /// <para><b>Sign convention.</b> Every table below is stored in <b>Unity radians</b>, not the
    /// oracle's degrees: AS3 measures angles with Y growing <i>down</i> while this port's
    /// <c>WeaponRuntimeState.Rot</c> comes out of <c>Mathf.Atan2(dy, dx)</c> in a Y-up world, so the
    /// conversion is <c>rad_unity = -rad_flash</c>. The tables are therefore the oracle's numbers with
    /// the sign flipped, and the same flip is why the elevation clamp below reads "at most 30° above
    /// horizontal" rather than the oracle's literal interval.</para>
    ///
    /// <para><b>Why a static class.</b> A <c>MonoBehaviour</c> cannot be exercised offline, and these are
    /// pure functions of (tip, placement, facing). Written on the brain they would be reachable only
    /// through a scene, which is how "it looks right" ships.</para>
    /// </summary>
    public static class TurretSweepMath
    {
        /// <summary>Degrees → radians, spelled out so the tables below stay literal.</summary>
        const float D = Mathf.Deg2Rad;

        /// <summary>
        /// <c>UnitTurret.as:615-621</c>'s <c>aiTCh = this.period</c>: how many ticks the mount holds one
        /// watch angle before stepping to the next. <c>:193</c> shortens it to 80 for tip 4 (the
        /// <c>combat</c> turret), which is the only mount that watches faster than the default.
        /// </summary>
        public static int PeriodTicks(int tip)
        {
            return tip == 4 ? 80 : 120;
        }

        /// <summary>
        /// <c>UnitTurret.as:30</c>'s <c>watchDrot</c> — radians per frame while watching
        /// (<c>aiState 1</c>). The oracle overwrites the field per mount in <c>putLoc</c>:
        /// <c>:193</c> tip 4 → 0.05, <c>:199</c> tip 1 → 0.1, <c>:233</c> tip 3 → 0.01, everything else
        /// keeps the declared 0.015.
        /// </summary>
        public static float WatchDrot(int tip)
        {
            switch (tip)
            {
                case 4: return 0.05f;
                case 1: return 0.1f;
                case 3: return 0.01f;
                default: return 0.015f;
            }
        }

        /// <summary>
        /// <c>UnitTurret.as:29</c>'s <c>actDrot</c> — radians per frame while committed
        /// (<c>aiState 3</c>, <c>:702</c>). One value for every mount; named so the two rates cannot be
        /// confused at a call site.
        /// </summary>
        public const float ActDrot = 0.1f;

        /// <summary>
        /// The <c>drot</c> a hidden mount uses — <c>UnitTurret.as:661</c>'s <c>currentWeapon.drot =
        /// 0.5</c> in <c>aiState 0</c>. Fast because a shutter that is about to open must have its barrel
        /// already parked at <c>π/2</c> (<c>:227</c>).
        /// </summary>
        public const float HiddenDrot = 0.5f;

        /// <summary>
        /// <c>UnitTurret.putLoc</c>'s <c>aRot</c> table for a mount — the angles the barrel walks while
        /// watching, in <b>Unity radians</b>.
        ///
        /// <code>
        /// tip 0,4 : [30, 150]                        (:188-193)
        /// tip 1   : [-15, -165]                      (:196-200)
        /// tip 2,5 : [45, 135, -135, -45]             (:202-231), then @vis narrows it
        /// tip 3   : facing right -> [-15, 15]        (:234-246)
        ///           facing left  -> [-165, 165]
        /// </code>
        ///
        /// <para><b>The <c>@vis</c> overrides are the wall/ceiling mounts' aiming half-plane</b>
        /// (<c>:211-229</c>): <c>down</c>→<c>[45,135]</c>, <c>up</c>→<c>[-45,-135]</c>,
        /// <c>left</c>→<c>[135,-135]</c>, <c>right</c>→<c>[45,-45]</c>. Note the oracle reads the
        /// attribute <i>before</i> the room mirror flips it (<c>:205-210</c>), so the caller passes the
        /// already-mirrored text.</para>
        ///
        /// <para><b>Why tip 3 depends on the facing.</b> The arm turret is bolted to a wall bracket and
        /// cannot turn around (<c>noTurn = true</c>, <c>:245</c>), so its table is built from
        /// <c>storona</c> rather than from a placement attribute.</para>
        /// </summary>
        /// <param name="tip">The mount tip, 0-5. Any other value falls back to the floor table.</param>
        /// <param name="vis">The placement's <c>@vis</c>, already mirrored; null/empty when absent.</param>
        /// <param name="facingSign">The mount's <c>storona</c>, used by tip 3 only.</param>
        public static float[] SweepTable(int tip, string vis, int facingSign)
        {
            switch (tip)
            {
                case 1:
                    // flash [-15,-165] -> unity [15,165]
                    return new[] { 15f * D, 165f * D };

                case 2:
                case 5:
                    return WallTable(vis);

                case 3:
                    // flash storona>0 [-15,15] -> unity [15,-15]; storona<0 [-165,165] -> [165,-165]
                    return facingSign > 0
                        ? new[] { 15f * D, -15f * D }
                        : new[] { 165f * D, -165f * D };

                case 0:
                case 4:
                default:
                    // flash [30,150] -> unity [-30,-150]
                    return new[] { -30f * D, -150f * D };
            }
        }

        /// <summary>
        /// The wall/ceiling table, with <c>UnitTurret.as:211-229</c>'s four <c>@vis</c> overrides applied.
        /// </summary>
        static float[] WallTable(string vis)
        {
            switch (vis)
            {
                // flash [45,135] -> unity [-45,-135]
                case "down":  return new[] { -45f * D, -135f * D };
                // flash [-45,-135] -> unity [45,135]
                case "up":    return new[] { 45f * D, 135f * D };
                // flash [135,-135] -> unity [-135,135]
                case "left":  return new[] { -135f * D, 135f * D };
                // flash [45,-45] -> unity [-45,45]
                case "right": return new[] { -45f * D, 45f * D };
                default:
                    // flash [45,135,-135,-45] -> unity [-45,-135,135,45]
                    return new[] { -45f * D, -135f * D, 135f * D, 45f * D };
            }
        }

        /// <summary>
        /// <c>UnitTurret.as:186-246</c>'s <c>currentWeapon.fixRot</c> — <b>which</b> elevation limit a
        /// mount gets. The limit itself is applied by the weapon
        /// (<see cref="PFE.Systems.Weapons.WeaponRotationMath.ClampFixRot"/>, from
        /// <c>Weapon.as:1146-1178</c>); this is only the turret's table of who gets what.
        ///
        /// <code>
        /// fixRot 1 : floor / combat  — at most 30 deg above horizontal   (:188, :193)
        /// fixRot 2 : arm, right      — +-30 deg around horizontal        (:237)
        /// fixRot 3 : arm, left       — +-30 deg around horizontal, left  (:241)
        /// fixRot 0 : no limit                                            (the default field, :94)
        /// </code>
        ///
        /// <para>Lives here rather than in PFE.Sim because the <i>assignment</i> is turret data: the
        /// oracle writes it from <c>putLoc</c>, next to the sweep tables it belongs with.</para>
        /// </summary>
        public static int FixRot(int tip, int facingSign)
        {
            switch (tip)
            {
                case 0:
                case 4: return 1;
                case 3: return facingSign > 0 ? 2 : 3;
                default: return 0;
            }
        }

        /// <summary>
        /// The starting entry — <c>UnitTurret.as:248-250</c>:
        /// <c>nRot = floor(random * aRot.length)</c>, then <c>rot = forceRot = aRot[nRot]</c>.
        /// The caller supplies the draw so the pick stays assertable.
        /// </summary>
        public static int InitialIndex(int tableLength, float roll01)
        {
            if (tableLength <= 0) return 0;
            int index = (int)(roll01 * tableLength);
            if (index < 0) index = 0;
            if (index >= tableLength) index = tableLength - 1;
            return index;
        }

        /// <summary>
        /// The next watch entry — <c>UnitTurret.as:617-621</c>: <c>++nRot</c>, wrapping at the table end.
        /// </summary>
        public static int NextIndex(int index, int tableLength)
        {
            if (tableLength <= 0) return 0;
            index++;
            return index >= tableLength ? 0 : index;
        }

        /// <summary>
        /// The barrel angle a hidden mount parks at — <c>UnitTurret.as:227</c> and <c>:599</c> both write
        /// <c>forceRot = Math.PI / 2</c>, which in Flash's Y-down space is <b>straight down</b>. The
        /// physical direction is unchanged by the mirror; only the angle's sign is, so the port's value
        /// is <c>-π/2</c> — still pointing at the floor, which is where a retracted barrel sits.
        /// </summary>
        public const float HiddenParkedRot = -Mathf.PI / 2f;
    }
}
