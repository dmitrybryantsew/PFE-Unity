using System;

namespace PFE.Entities.Pets
{
    /// <summary>
    /// The seam between a summoned pet's health and the HUD's companion bar — the port of
    /// <c>GUI.setPet</c> (<c>GUI.as:1086-1113</c>).
    ///
    /// <para><b>What the oracle draws.</b> <c>vis.hpPet</c> is visible while a pet is out; its fill is
    /// <c>hp / maxhp</c>; it is hidden when <c>hp &lt;= 0</c>; the label is
    /// <c>floor(noPet / 30)</c> seconds while the pet is cooling down, and the icon is hidden while
    /// <c>noPet &gt; 0</c>. The port keeps the fraction and the seconds in one call, because the two are
    /// drawn by the same widget and splitting them would let a bar show a corpse.</para>
    ///
    /// <para><b>Why a static and not a broker.</b> The publisher is <see cref="PetCompanion"/>, which is
    /// added to the player at runtime with <c>AddComponent</c> — nothing injects it, so there is no
    /// <c>[Inject]</c> site for an <c>IPublisher&lt;T&gt;</c>. Same shape and same reason as
    /// <c>BossHealthBarFeed</c> and <c>ItemPickupFeedback</c>: the entity layer must not know what a HUD
    /// is.</para>
    /// </summary>
    public static class PetHealthBarFeed
    {
        /// <summary>
        /// A summoned pet's health as a fraction of its maximum, plus the remaining cooldown in seconds
        /// (<c>floor(noPet / 30)</c>). Raised once per sim tick while a pet is out.
        /// </summary>
        public static event Action<float, float> Reported;

        /// <summary>The bar must go away — no pet is out, or the summoned one was un-summoned.</summary>
        public static event Action Cleared;

        /// <summary>Publish the live pet's health.</summary>
        public static void Report(float fraction, float cooldownSeconds)
        {
            Action<float, float> reported = Reported;
            if (reported != null)
            {
                reported(fraction, cooldownSeconds);
            }
        }

        /// <summary>Hide the bar.</summary>
        public static void Clear()
        {
            Action cleared = Cleared;
            if (cleared != null)
            {
                cleared();
            }
        }
    }
}
