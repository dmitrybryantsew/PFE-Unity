namespace PFE.Tests.Editor.Map.Actions
{
    /// <summary>
    /// Test double for <see cref="PFE.Systems.Campaign.ITravelMapHost"/>.
    ///
    /// <para>It counts the three verbs <b>separately</b>, which is the point. The load-bearing assertion
    /// about the camp's wall map is not "travel ended up unlocked" but "the caller used the three-step
    /// <c>GrantTravelAndOpenMap</c> and not <c>UnlockTravel</c> followed by <c>OpenTravelMap</c>". A
    /// double that folded the three into one flag could not tell those apart, and the real host
    /// (<c>CampaignManager</c>) is exercised separately — see <c>TravelMapHostTests</c>.</para>
    ///
    /// <para><see cref="GrantTravelAndOpenMap"/> models the recompute rather than calling the other two
    /// members, so the counts stay direct-call counts: the middle step <i>clears</i> the flag (the
    /// player is not in a base land here) and the third puts it back.</para>
    /// </summary>
    internal sealed class FakeTravelMapHost : PFE.Systems.Campaign.ITravelMapHost
    {
        /// <summary>How many times <see cref="GrantTravelAndOpenMap"/> was called.</summary>
        public int GrantAndOpenCalls;

        /// <summary>How many times <see cref="UnlockTravel"/> was called <b>directly</b>.</summary>
        public int UnlockCalls;

        /// <summary>How many times <see cref="OpenTravelMap"/> was called <b>directly</b>.</summary>
        public int OpenCalls;

        public bool TravelUnlocked { get; private set; }

        public void UnlockTravel()
        {
            UnlockCalls++;
            TravelUnlocked = true;
        }

        public void OpenTravelMap()
        {
            OpenCalls++;
            TravelUnlocked = false;
        }

        public void GrantTravelAndOpenMap()
        {
            GrantAndOpenCalls++;
            TravelUnlocked = true;   // grant
            TravelUnlocked = false;  // onoff -> setButtons, and this is not a base land
            TravelUnlocked = true;   // the trailing re-grant
        }
    }
}
