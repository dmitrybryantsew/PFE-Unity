using UnityEngine;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// ScriptableObject configuration for physics constants.
    /// Based on ActionScript Part.as physics system.
    /// </summary>
    [CreateAssetMenu(fileName = "PhysicsConfig", menuName = "PFE/Physics Config")]
    public class PhysicsConfig : ScriptableObject
    {
        [Header("Movement Physics")]
        [Tooltip("Horizontal movement acceleration")]
        [Range(0f, 100f)]
        public float moveAcceleration = 50f;

        [Tooltip("Maximum horizontal velocity")]
        [Range(0f, 50f)]
        public float maxHorizontalSpeed = 10f;

        [Tooltip("Jump impulse velocity")]
        [Range(0f, 50f)]
        public float jumpImpulse = 15f;

        [Header("Friction & Braking")]
        [Tooltip("Brake coefficient applied to velocity each frame (0-1, where 1 = no friction)")]
        [Range(0.8f, 1f)]
        public float defaultBrake = 0.95f;

        [Tooltip("Air resistance (brake coefficient while in air)")]
        [Range(0.9f, 1f)]
        public float airBrake = 0.99f;

        [Tooltip("Ground friction")]
        [Range(0.7f, 1f)]
        public float groundBrake = 0.9f;

        [Header("Gravity")]
        [Tooltip("Gravity acceleration (added to dy each frame)")]
        [Range(-100f, 0f)]
        public float gravity = -30f;

        [Tooltip("Terminal falling velocity")]
        [Range(-100f, 0f)]
        public float maxFallSpeed = -40f;

        [Header("Rotation Physics")]
        [Tooltip("Rotation speed (dr - degrees per frame)")]
        [Range(-360f, 360f)]
        public float rotationSpeed = 5f;

        [Tooltip("Rotation friction (0-1, where 1 = no friction)")]
        [Range(0.8f, 1f)]
        public float rotationBrake = 0.95f;

        [Header("Particle Physics")]
        [Tooltip("Default particle lifetime in frames")]
        [Range(1, 300)]
        public int defaultParticleLifetime = 20;

        [Tooltip("Frames for particle fade out (alpha = liv/fadeFrames)")]
        [Range(1, 20)]
        public int particleFadeFrames = 10;

        [Header("Tile-Based Movement")]
        [Tooltip("Size of one tile in world units")]
        public float tileSize = 1f;

        [Tooltip("Snap position to tile grid when within this distance")]
        public float tileSnapDistance = 0.1f;
    }
}
