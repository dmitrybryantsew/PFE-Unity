using UnityEngine;

namespace PFE.Entities.Enemies
{
    /// <summary>
    /// Contract for an enemy behavioral brain.
    /// Decoupled from physical kinematics and rendering.
    /// </summary>
    public interface IEnemyBrain
    {
        /// <summary>Current active behavioral state.</summary>
        EnemyAIState CurrentState { get; }

        /// <summary>Situational data container.</summary>
        EnemyBlackboard Blackboard { get; }

        /// <summary>Perception sensor engine.</summary>
        EnemySensors Sensors { get; }

        /// <summary>
        /// The point the sensors cast from and hear from, in world pixels.
        ///
        /// <para><b>Exposed so a tool can draw the ray the sensor actually casts.</b> An overlay that
        /// re-derived the eye from <c>transform.position</c> and the unit's height would be a second
        /// implementation of the same expression, and the first time the two drifted the drawn cone
        /// would be evidence about the overlay rather than about the AI. This is a projection of
        /// <c>EnemyBrain.GetEyePositionPixels()</c>, not a copy of its arithmetic.</para>
        /// </summary>
        Vector2 EyePositionPixels { get; }

        /// <summary>Forces transition to a specified state.</summary>
        void SetState(EnemyAIState newState);

        /// <summary>Informs brain of incoming damage to trigger alert / social wake-up.</summary>
        void OnDamaged(float amount, Vector2 damageSourcePx);
    }
}
