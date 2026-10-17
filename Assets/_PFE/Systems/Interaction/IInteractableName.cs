namespace PFE.Systems.Interaction
{
    /// <summary>
    /// An <see cref="IInteractable"/> that can <b>name itself</b> for the world-anchored interaction
    /// prompt — the port's <c>celObj.nazv</c> (<c>GUI.as:1286-1299</c>).
    ///
    /// <para><b>Why a separate, additive interface.</b> <see cref="IInteractable"/> is deliberately
    /// minimal (an action text, a reach test, and the act itself) and adding a member to it would break
    /// every implementor and every test double at the point of declaration — the same reason
    /// <see cref="IHoldInteractable"/> is derived rather than folded in. A name is also genuinely
    /// optional: a thing can be interactable and anonymous, and the prompt then shows only the action
    /// text.</para>
    ///
    /// <para><b>What "the name" means.</b> AS3 reads <c>celObj.nazv</c> — the object's display name off
    /// its <c>AllData</c> row, not its id. The port resolves the same value from the object's
    /// definition (<c>MapObjectDefinition.displayName</c>, set by <c>MapObjectDefinitionImporter</c>),
    /// falling back to the id when a definition has no name, so an unnamed object still labels
    /// something.</para>
    /// </summary>
    public interface IInteractableName
    {
        /// <summary>The name to float over the object. May be empty; the prompt then shows only the action text.</summary>
        string DisplayName { get; }
    }
}
