using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Weapons;
using PFE.Systems.Weapons.Controllers;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// The two arms of AS3 <c>Weapon.actions()</c>'s position rule (<c>Weapon.as:1076-1086</c>):
    /// a rigidly mounted weapon <b>snaps</b> to the owner's hold point, a carried one <b>eases</b>
    /// toward it at 1/5 per flash frame.
    ///
    /// <para><b>Why this fixture exists.</b> The port tested "is this the first frame" and eased for
    /// every unit, so every moving enemy dragged its gun <c>4·v</c> px behind its own body while
    /// <c>EnemyWeaponMount</c> fired from the un-lagged hold point — the barrel and the round
    /// disagreed. Reported as "its a bit off" about the robots, whose <c>weaponKrep</c> is 1 across the
    /// whole family. See <c>docs/EnemyImplementationGuides/AUDIT_robot_weapon_attachment_2026-10-19.md</c>
    /// §R1.</para>
    ///
    /// <para><b>Every assertion has a control.</b> The rigid test alone would pass against a rule that
    /// snapped unconditionally, so <see cref="CarriedWeapon_Eases_Control"/> pins the other arm — and
    /// the easing test alone would pass against the pre-fix code, which is the state this fixture is
    /// guarding. Neither is evidence without the other.</para>
    /// </summary>
    public class WeaponRigidMountTests
    {
        private const float Dt = 1f / 30f;   // one AS3 flash frame

        private static WeaponDefinition MakeDef()
        {
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            def.weaponId     = "test_rigid_mount";
            def.weaponType   = WeaponType.Guns;
            def.baseDamage   = 10f;
            def.rapid        = 10f;
            def.magazineSize = 30;
            return def;
        }

        /// <summary>Advance one flash frame with a hold point and an aim point.</summary>
        private static void Frame(RangedWeaponController ctrl, Vector2 hold, Vector2 aim)
            => ctrl.Tick(Dt, hold, hold, aim);

        [Test]
        public void RigidMount_SnapsToTheHoldPoint_OnEveryFrame()
        {
            var def = MakeDef();
            var state = new WeaponRuntimeState(def);
            var ctrl = new RangedWeaponController(state, null, null, null, null);
            ctrl.SnapToHoldPoint = true;   // AS3 `krep > 0`

            try
            {
                // Frame 1 establishes the position; frame 2 moves the owner 1 u and is the assertion.
                Frame(ctrl, new Vector2(10f, 5f), new Vector2(20f, 5f));
                Frame(ctrl, new Vector2(11f, 5f), new Vector2(20f, 5f));

                // Exact, not approximate: the oracle ASSIGNS X/Y, it does not approach them.
                Assert.AreEqual(11f, state.X, 1e-4f,
                    "a rigid mount must be ON the hold point the same frame the owner moves");
                Assert.AreEqual(5f, state.Y, 1e-4f);
            }
            finally
            {
                ctrl.Dispose();
                Object.DestroyImmediate(def);
            }
        }

        [Test]
        public void CarriedWeapon_Eases_Control()
        {
            var def = MakeDef();
            var state = new WeaponRuntimeState(def);
            var ctrl = new RangedWeaponController(state, null, null, null, null);
            ctrl.SnapToHoldPoint = false;  // AS3 `krep == 0` — the player and 14 authored rows

            try
            {
                Frame(ctrl, new Vector2(10f, 5f), new Vector2(20f, 5f));
                Frame(ctrl, new Vector2(11f, 5f), new Vector2(20f, 5f));

                // `X += (weaponX - X) / 5` from 10 toward 11 = 10.2. Asserting the *strict* inequality
                // as well as the value, so a rule that snapped regardless cannot pass this.
                Assert.AreEqual(10.2f, state.X, 1e-4f);
                Assert.Less(state.X, 11f,
                    "the easing arm must NOT be on the hold point after one frame — otherwise the " +
                    "rigid test above proves nothing");
            }
            finally
            {
                ctrl.Dispose();
                Object.DestroyImmediate(def);
            }
        }

        /// <summary>
        /// The rigid arm aims along the <b>body's</b> facing and lets only the elevation follow the
        /// cursor: <c>atan2(celY - Y, |celX - X| * owner.storona)</c> (<c>Weapon.as:1080</c>).
        ///
        /// <para>The case that tells the two formulas apart: a right-facing mount whose target is
        /// <b>behind</b> it. The oracle keeps the barrel forward (a small positive angle, elevation
        /// only); aiming at the cursor outright would point it backwards (≈π).</para>
        /// </summary>
        [Test]
        public void RigidMount_AimsAlongTheBodyFacing_NotAtTheCursor()
        {
            var def = MakeDef();
            var state = new WeaponRuntimeState(def);
            var ctrl = new RangedWeaponController(state, null, null, null, null);
            ctrl.SnapToHoldPoint  = true;
            ctrl.OwnerFacingSign  = 1f;   // storona = +1, facing right

            try
            {
                // Hold at x = 10, target at x = 0 — i.e. directly behind, level with the gun.
                Frame(ctrl, new Vector2(10f, 5f), new Vector2(0f, 5f));

                // atan2(0, |0-10| * +1) = atan2(0, 10) = 0 → barrel horizontal, pointing right.
                Assert.AreEqual(0f, state.Rot, 1e-4f,
                    "a rigid mount must not swing its barrel 180 degrees through its own torso");
                Assert.Less(Mathf.Abs(state.Rot), Mathf.PI * 0.5f,
                    "the barrel must stay on the facing side of the mount");
            }
            finally
            {
                ctrl.Dispose();
                Object.DestroyImmediate(def);
            }
        }

        [Test]
        public void RigidMount_MirrorsTheAimWithTheFacing()
        {
            var def = MakeDef();
            var state = new WeaponRuntimeState(def);
            var ctrl = new RangedWeaponController(state, null, null, null, null);
            ctrl.SnapToHoldPoint = true;
            ctrl.OwnerFacingSign = -1f;   // storona = -1, facing left

            try
            {
                // Hold at x = 10, target at x = 20 — behind a left-facing mount, and ABOVE it.
                Frame(ctrl, new Vector2(10f, 5f), new Vector2(20f, 15f));

                // atan2(+10, |20-10| * -1) = atan2(10, -10) = 3π/4 → barrel points left and up.
                Assert.AreEqual(Mathf.PI * 0.75f, state.Rot, 1e-3f);
            }
            finally
            {
                ctrl.Dispose();
                Object.DestroyImmediate(def);
            }
        }
    }
}
