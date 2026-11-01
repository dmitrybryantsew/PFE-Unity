namespace PFE.Systems.Audio
{
    /// <summary>
    /// Where a <b>code-spawned</b> object finds the sound service — the same problem
    /// <c>LootRuntime</c> solves for the loot tables, and solved the same way: a settable static with one
    /// wiring pass.
    ///
    /// <para><b>Why not constructor injection.</b> <see cref="PFE.Systems.Inventory.WorldItemPickup"/> and
    /// <see cref="PFE.Systems.Map.Rendering.ContainerPropPresenter"/> are created by
    /// <c>new GameObject(...)</c> / by the room streaming code, not by VContainer, so there is no
    /// injection point. <c>ISoundService</c> is registered in
    /// <see cref="PFE.Core.Installers.GameLifetimeScope"/> from the scene's one <c>SoundService</c>, and
    /// <see cref="Current"/> is assigned in that same block — so the two cannot disagree about which
    /// service is live.</para>
    ///
    /// <para><b>Null is a legitimate answer, not an error.</b> A scene with no <c>SoundService</c> is
    /// already a silent game (<c>GameLifetimeScope</c> warns once about it), and every caller here is a
    /// sound — so a missing service must leave the game playable and quiet rather than throw. Callers
    /// therefore use <c>SoundBus.Current?.Play(...)</c>.</para>
    ///
    /// <para><b>Settable so a test can substitute a recorder.</b> Same reason
    /// <c>LootRuntime.Rng</c> has a setter: the assertions worth making are about <i>which id</i> was
    /// asked for and at what volume, and a fixture cannot observe that through a real <c>AudioSource</c>.</para>
    /// </summary>
    public static class SoundBus
    {
        /// <summary>The live sound service, or <c>null</c> when the scene has none.</summary>
        public static ISoundService Current { get; set; }
    }
}
