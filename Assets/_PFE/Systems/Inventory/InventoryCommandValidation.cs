namespace PFE.Systems.Inventory
{
    /// <summary>
    /// The input contract for an <see cref="InventoryCommand"/> — the rules a request must satisfy
    /// before any state is touched.
    ///
    /// <para><b>Deliberately Unity-free.</b> No <c>UnityEngine</c> type is referenced and no engine
    /// call is made, so this runs in the offline wall
    /// (<c>.workbuddy-ai/tmp/wallrun</c>) where <c>ScriptableObject.CreateInstance</c> and every
    /// <c>Debug.Log</c> throw <c>SecurityException: ECall methods must be packaged into a system
    /// module</c>. That matters because it is the one half of the seam that <i>can</i> be proved
    /// offline, and a rule proved only by a build is not proved (rule #7).</para>
    ///
    /// <para><b>Why this is a separate concern from applying the command.</b> Under host-authoritative
    /// co-op a command arrives from a client, and the host must reject a malformed one before it
    /// reaches the inventory. The same check runs in single-player, so the two paths cannot drift into
    /// "the client validated it, the host did not".</para>
    ///
    /// <para><b>What is NOT here.</b> Resolution of <see cref="InventoryCommand.Id"/> to a definition,
    /// and the "is there enough to remove" query, both need the live inventory or the content registry
    /// and therefore live in <see cref="LocalInventoryCommandSink"/>. This type answers only
    /// "is the request well formed", which is answerable from the command alone.</para>
    /// </summary>
    public static class InventoryCommandValidation
    {
        /// <summary>
        /// Whether <paramref name="kind"/> is a command this build knows how to apply.
        ///
        /// <para>An unknown value is a real case, not a hypothetical: a command can arrive from a peer
        /// running a newer build, and silently ignoring it is how a desync is born.</para>
        /// </summary>
        public static bool IsKnownKind(InventoryCommandKind kind)
        {
            switch (kind)
            {
                case InventoryCommandKind.AddItem:
                case InventoryCommandKind.RemoveItem:
                case InventoryCommandKind.AddArmor:
                case InventoryCommandKind.ConsumeAmmo:
                case InventoryCommandKind.DropItem:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The reason to reject <paramref name="command"/>, or <c>null</c> when it is well formed.
        ///
        /// <para><b>Returns the reason rather than a bool</b> so the caller can print it. A rejection
        /// with no stated reason is indistinguishable from a command that never arrived.</para>
        /// </summary>
        public static string RejectReason(InventoryCommand command)
        {
            if (!IsKnownKind(command.Kind))
                return $"unsupported command kind {(int)command.Kind}";

            if (string.IsNullOrEmpty(command.Id))
                return $"no id for {command.Kind}";

            if (command.Quantity <= 0)
                return $"quantity must be positive (was {command.Quantity})";

            return null;
        }

        /// <summary>Convenience for callers that want a bool. <see cref="RejectReason"/> carries the why.</summary>
        public static bool IsWellFormed(InventoryCommand command) => RejectReason(command) == null;
    }
}
