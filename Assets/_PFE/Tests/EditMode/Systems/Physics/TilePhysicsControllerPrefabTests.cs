using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using PFE.Systems.Physics;

namespace PFE.Tests.EditMode.Systems.Physics
{
    /// <summary>
    /// P0-1 regression guard. See docs/Roadmap/01_P0_CRITICAL_DEFECT_FIXES.md (Defect 1).
    ///
    /// Two TilePhysicsController instances on one GameObject both write
    /// transform.position every FixedUpdate, while GetComponent&lt;IMovementMotor&gt;()
    /// only ever returns the first. The result is input state living in one instance
    /// and rendered position in the other — intermittent jitter, phantom grounded
    /// states and dropped input. The class now carries [DisallowMultipleComponent],
    /// but that attribute does not repair pre-existing duplicates, hence this test.
    /// </summary>
    [TestFixture]
    public class TilePhysicsControllerPrefabTests
    {
        private const string PlayerPrefabPath = "Assets/_PFE/Prefabs/Player.prefab";

        [Test]
        public void PlayerPrefab_HasExactlyOneTilePhysicsController()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            Assert.IsNotNull(prefab, "Player.prefab not found at " + PlayerPrefabPath);

            TilePhysicsController[] motors = prefab.GetComponents<TilePhysicsController>();
            Assert.AreEqual(
                1,
                motors.Length,
                "Player.prefab must have exactly one TilePhysicsController. " +
                "Duplicates desync dx/dy, currentRoom and the pixel position.");
        }

        /// <summary>
        /// The surviving instance is the one callers resolve via
        /// GetComponent&lt;IMovementMotor&gt;(). If it ever stops being the first in
        /// component order, every caller silently re-points — assert that too.
        /// </summary>
        [Test]
        public void PlayerPrefab_MotorIsTheFirstComponentOfItsType()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            Assert.IsNotNull(prefab, "Player.prefab not found at " + PlayerPrefabPath);

            IMovementMotor resolved = prefab.GetComponent<IMovementMotor>();
            Assert.IsNotNull(resolved, "Player.prefab exposes no IMovementMotor.");
            Assert.IsInstanceOf<TilePhysicsController>(resolved,
                "IMovementMotor on Player.prefab must resolve to TilePhysicsController.");
            Assert.AreSame(
                prefab.GetComponents<TilePhysicsController>()[0],
                resolved,
                "GetComponent<IMovementMotor>() must resolve the first TilePhysicsController.");
        }

        /// <summary>
        /// Scene-wide variant: catches a duplicate added to any scene instance or
        /// another prefab, not just Player.prefab.
        /// </summary>
        [Test]
        public void NoPrefabInProject_HasDuplicateTilePhysicsController()
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                // GetComponentsInChildren covers nested GameObjects too.
                TilePhysicsController[] motors =
                    prefab.GetComponentsInChildren<TilePhysicsController>(true);

                for (int i = 0; i < motors.Length; i++)
                {
                    int siblings = motors[i].GetComponents<TilePhysicsController>().Length;
                    Assert.AreEqual(
                        1,
                        siblings,
                        "Prefab '" + path + "' has " + siblings +
                        " TilePhysicsController components on GameObject '" +
                        motors[i].name + "'. Exactly one is required.");
                }
            }
        }
    }
}
