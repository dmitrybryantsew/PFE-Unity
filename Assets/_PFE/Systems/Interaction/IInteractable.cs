using UnityEngine;

namespace PFE.Systems.Interaction
{
    /// <summary>
    /// Interface for interactive world objects (doors, containers, terminals, switches, etc.).
    /// Direct counterpart to AS3 fe/serv/Interact.as interface logic.
    /// </summary>
    public interface IInteractable
    {
        /// <summary>
        /// Display action name for HUD/prompts (e.g. \"Open\", \"Close\", \"Search\", \"Use\").
        /// Mirrors AS3 Interact.actionText.
        /// </summary>
        string ActionText { get; }

        /// <summary>
        /// Whether the user can currently interact with this object.
        /// </summary>
        bool CanInteract(GameObject user);

        /// <summary>
        /// Trigger interaction on the object.
        /// </summary>
        void Interact(GameObject user);
    }
}