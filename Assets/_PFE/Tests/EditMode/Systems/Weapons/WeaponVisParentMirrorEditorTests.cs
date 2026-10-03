using NUnit.Framework;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Editor-only guard that <see cref="WeaponVisMath.ParentScaleSign(Transform)"/> reads Unity's real
    /// parent chain rather than only the immediate node.
    ///
    /// <para><b>Why this is a separate fixture.</b> It arranges with <see cref="GameObject"/>, whose
    /// constructor is an ECall, so under the offline harness
    /// (<c>.workbuddy-ai/tools/agentverify/</c>) it dies with <c>SecurityException: ECall methods must
    /// be packaged into a system module</c> before any assertion is reached. Splitting it out keeps
    /// <see cref="WeaponVisMathTests"/> — which pins the mirror <i>arithmetic</i> through
    /// <see cref="WeaponVisMath.ChainScaleSign"/> and <see cref="WeaponVisMath.ChainStep"/> — entirely
    /// engine-free, so that fixture's verdict is real.</para>
    ///
    /// <para><b>What is left to check here.</b> Only the plumbing: that <c>Transform.parent</c> and
    /// <c>Transform.localScale</c> compose the way the walk assumes, and that a mirror one level above
    /// the immediate parent is seen. That is a Unity fact, not this project's logic, and only the editor
    /// can execute it. It is kept because the fix depends on it — if the walk silently stopped at the
    /// immediate parent, <c>ParentScaleSign</c> would always return <c>1</c> and the double-flip would
    /// come back with nothing going red offline.</para>
    /// </summary>
    [TestFixture]
    public class WeaponVisParentMirrorEditorTests
    {
        private const float Tolerance = 1e-4f;

        [Test]
        public void ParentScaleSign_ReadsAMirrorAndTreatsNoParentAsUnmirrored()
        {
            Assert.AreEqual(1f, WeaponVisMath.ParentScaleSign(null), Tolerance,
                "a parentless vis has no mirror to divide out");

            var root = new GameObject("root");
            var child = new GameObject("child");
            try
            {
                child.transform.SetParent(root.transform);

                root.transform.localScale = new Vector3(1f, 1f, 1f);
                Assert.AreEqual(1f, WeaponVisMath.ParentScaleSign(child.transform), Tolerance);

                root.transform.localScale = new Vector3(-1f, 1f, 1f);
                Assert.AreEqual(-1f, WeaponVisMath.ParentScaleSign(child.transform), Tolerance,
                    "this is the case the unit root creates when the player faces left");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ParentScaleSign_SeesThroughAnInterveningNode()
        {
            // The real rig: vis → Weapon node → unit root. The mirror is a grandparent, and the
            // immediate parent is unscaled — so a walk that stopped one level early would answer +1.
            var root = new GameObject("root");
            var weapon = new GameObject("weapon");
            var vis = new GameObject("vis");
            try
            {
                weapon.transform.SetParent(root.transform);
                vis.transform.SetParent(weapon.transform);

                root.transform.localScale = new Vector3(-1f, 1f, 1f);
                Assert.AreEqual(-1f, WeaponVisMath.ParentScaleSign(vis.transform), Tolerance);

                // A compensating scale on the intermediate node cancels it, and the sign must follow.
                weapon.transform.localScale = new Vector3(-1f, 1f, 1f);
                Assert.AreEqual(1f, WeaponVisMath.ParentScaleSign(vis.transform), Tolerance);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
