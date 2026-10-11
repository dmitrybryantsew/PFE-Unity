namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// The campaign-level actions a map-object script can invoke — the ones that are not object-local
    /// and therefore do not belong in <c>ObjectActionDispatcher</c>.
    ///
    /// <para>Implemented by <c>CampaignManager</c> and injected into <see cref="AreaTriggerSystem"/>.
    /// Every method names the oracle it ports. This interface exists so the script vocabulary can be
    /// tested with a fake host instead of a live campaign.</para>
    /// </summary>
    public interface ILandScriptHost
    {
        /// <summary>
        /// AS3 <c>upland</c> → <c>Game.upLandLevel()</c> (<c>Game.as:474-481</c>) — the "go lower" step.
        /// Increments the current land's <c>landStage</c> at most once per entry.
        /// </summary>
        /// <returns><c>true</c> if this call moved <c>landStage</c>.</returns>
        bool UpLandLevel();

        /// <summary>AS3 <c>openland</c> (<c>Script.as:458-468</c>) — unlock a land on the travel map.</summary>
        /// <returns><c>false</c> when the land id is unknown (AS3 traces <c>"error land"</c>).</returns>
        bool OpenLand(string landId);

        /// <summary>AS3 <c>refill</c> → <c>Land.refill()</c> (<c>Land.as:1481-1496</c>).</summary>
        void RefillVendors();

        /// <summary>AS3 <c>trigger</c> (<c>Script.as:424-434</c>).</summary>
        void SetTrigger(string triggerName, int value);

        /// <summary>AS3 <c>passed</c> (<c>Script.as:469-472</c>).</summary>
        void MarkPassed();

        /// <summary>
        /// AS3 <c>exit</c> (the object's <c>allact</c>) → <c>Game.gotoNextLevel()</c>
        /// (<c>Game.as:464-472</c>) — rebuild the same land at the new <c>landStage</c>.
        /// </summary>
        void GotoNextLevel();

        /// <summary>
        /// AS3 <c>gotoland</c> (<c>Script.as:443-456</c>).
        /// </summary>
        /// <param name="landId">The destination land id (<c>val</c>).</param>
        /// <param name="n">Branch selector: 2 = force regenerate, 1 = use <paramref name="coordinates"/>,
        /// anything else = plain.</param>
        /// <param name="coordinates">"x:y" entry override, or null.</param>
        void GotoLand(string landId, int n, string coordinates);
    }
}
