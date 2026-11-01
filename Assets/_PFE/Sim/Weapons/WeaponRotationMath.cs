using UnityEngine;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// AS3 <c>Weapon.actions()</c>'s barrel-rotation block, <c>Weapon.as:1087-1178</c>, as plain
    /// arithmetic — the <c>drot</c> easing and the <c>fixRot</c> elevation clamp.
    ///
    /// <para><b>Why this is a separate file and not a method on the controller.</b>
    /// <c>RangedWeaponController</c> is a <c>MonoBehaviour</c>-free class but it still needs a
    /// <c>WeaponRuntimeState</c> to do anything, and the easing has three genuinely tricky branches (the
    /// <c>|Δ| &gt; π</c> wrap, the nearly-a-full-turn snap, and the post-ease clamp). Written inline they
    /// could only be checked by reading them; written here they are a pure function of four numbers.</para>
    ///
    /// <para><b>Lives in PFE.Sim, not PFE.View.</b> <c>PFE.Sim</c> does not reference <c>PFE.View</c>
    /// (<c>PFE.Sim.asmdef</c> lists only ModAPI and the Unity packages), so the turret's own tables in
    /// <c>PFE.Entities.Enemies.Archetypes.TurretSweepMath</c> cannot be reached from here. The split is
    /// the right one anyway: this is <i>Weapon</i> behaviour that every weapon shares, while the turret
    /// tables are mount data.</para>
    /// </summary>
    public static class WeaponRotationMath
    {
        /// <summary>
        /// One frame of the oracle's easing: where <paramref name="rot"/> moves toward
        /// <paramref name="rot2"/> by at most <paramref name="drot"/>·<paramref name="drotMult"/> radians.
        /// </summary>
        /// <param name="rot">Current barrel angle, radians.</param>
        /// <param name="rot2">Target angle, radians.</param>
        /// <param name="drot">Per-frame step, radians. <b><c>&lt;= 0</c> snaps</b> — AS3's
        /// <c>if(rdrot == 0) { rot = rot2; ready = true; }</c> arm (<c>Weapon.as:1100-1104</c>).</param>
        /// <param name="drotMult">AS3 <c>drotMult</c>; 1 is the declared default.</param>
        /// <param name="rotOut">The new angle.</param>
        /// <param name="ready">AS3 <c>Weapon.ready</c> — true when the barrel is aligned, which is what
        /// gates <c>checkAvail</c>'s "can I shoot yet".</param>
        public static void Advance(float rot, float rot2, float drot, float drotMult,
                                   out float rotOut, out bool ready)
        {
            if (drot <= 0f)
            {
                rotOut = rot2;
                ready  = true;
                return;
            }

            float step  = drot * (drotMult > 0f ? drotMult : 1f);
            float delta = rot2 - rot;

            if (Mathf.Abs(delta) > Mathf.PI)
            {
                // AS3 :1113-1131. Inside the wrap arm it first asks whether the gap is nearly a full
                // turn, in which case the shortest path is no path at all and it SNAPS. Without that
                // test an eased barrel would crawl the long way round for a difference of ~2π.
                if (Mathf.Abs(delta) > Mathf.PI * 2f - step)
                {
                    rotOut = rot2;
                    ready  = true;
                }
                else
                {
                    rotOut = rot > rot2 ? rot + step : rot - step;
                    ready  = false;
                }
            }
            else if (delta > step)
            {
                rotOut = rot + step;
                ready  = false;
            }
            else if (delta < -step)
            {
                rotOut = rot - step;
                ready  = false;
            }
            else
            {
                rotOut = rot2;
                ready  = true;
            }

            // AS3 :1138-1144 — keep the angle inside (-π, π] so the comparison above never has to
            // reason about a multi-turn value.
            if (rotOut > Mathf.PI)  rotOut -= Mathf.PI * 2f;
            if (rotOut < -Mathf.PI) rotOut += Mathf.PI * 2f;
        }

        /// <summary>
        /// AS3 <c>Weapon.fixRot</c> — the mechanical elevation limit, applied <b>after</b> the easing
        /// (<c>Weapon.as:1146-1178</c>), so it can override the eased value outright.
        ///
        /// <para><b>The intervals are the oracle's, mirrored into the port's Y-up angle space.</b> AS3
        /// clamps flash <c>rot</c> out of <c>(-5π/6, -π/6)</c> for <c>fixRot 1</c>; since
        /// <c>rot_unity = -rot_flash</c>, that is the interval <c>(π/6, 5π/6)</c> here — "pointing more
        /// than 30° above horizontal" — snapped to whichever boundary is nearer. A floor turret can
        /// depress almost to straight down but elevates only 30°.</para>
        /// </summary>
        /// <param name="rot">Barrel angle in radians, Unity convention (+Y up).</param>
        /// <param name="fixRot">0 none, 1 floor/combat, 2 arm-right, 3 arm-left.</param>
        public static float ClampFixRot(float rot, int fixRot)
        {
            const float Sixth     = Mathf.PI / 6f;      // 30°
            const float FiveSixth = Mathf.PI * 5f / 6f; // 150°

            switch (fixRot)
            {
                case 1:
                    if (rot > Sixth && rot < FiveSixth)
                    {
                        rot = rot < Mathf.PI / 2f ? Sixth : FiveSixth;
                    }
                    return rot;

                case 2:
                    return Mathf.Clamp(rot, -Sixth, Sixth);

                case 3:
                    // AS3 :1170-1177 — a two-state snap: a left-facing arm can only point left.
                    if (rot > -FiveSixth && rot < 0f) return -FiveSixth;
                    if (rot >= 0f && rot < FiveSixth) return FiveSixth;
                    return rot;

                default:
                    return rot;
            }
        }
    }
}
