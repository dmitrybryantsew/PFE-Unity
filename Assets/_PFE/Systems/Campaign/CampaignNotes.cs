using System;
using System.Collections.Generic;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// The campaign's ordered note list — the port of <c>Game.notes</c>
    /// (<c>Game.as:666-673</c>, <c>:301-303</c>).
    ///
    /// <para><b>Why this is a type and not a <c>List&lt;string&gt;</c> on the manager.</b> The Oracle's
    /// list has three properties that a bare list would lose, and all three are load-bearing for the
    /// journal:</para>
    /// <list type="number">
    /// <item><description><b>Order is the display order's tie-breaker.</b> The journal shows the
    /// important notes in the order they were added and the ordinary ones <b>reversed</b>
    /// (<c>PipPageInfo.as:238-241</c>), so "which note came first" is on screen. A <c>HashSet</c>, which
    /// is what a "does the player have this note" API invites, would erase exactly that.</description></item>
    /// <item><description><b>An id can be added twice and must not be.</b> <c>addNote</c> is called from
    /// three places — the two seeds, <c>GUI.as:1783</c> and any script — and a duplicate would put the
    /// same note on two rows with two different positions.</description></item>
    /// <item><description><b>The three seeds are the whole of a fresh campaign's journal</b>
    /// (<c>Game.as:301-303</c>), and all three are <c>imp="3"</c>, so a new game shows three important
    /// notes and nothing else. That is a fact a fixture should be able to state.</description></item>
    /// </list>
    ///
    /// <para><b>Unity-free</b>, so the journal's ordering rule can be exercised against the real store
    /// from a plain shell.</para>
    /// </summary>
    public sealed class CampaignNotes
    {
        /// <summary>The three notes a new campaign starts with — <c>Game.as:301-303</c>, verbatim and
        /// in the Oracle's order.</summary>
        public static readonly string[] SeedIds = { "helpControl", "helpGl1", "helpGl2" };

        private readonly List<string> _ids = new List<string>();

        /// <summary>
        /// The process-wide store, resolved the way <c>CampaignManager.Current</c> is. Null until
        /// <see cref="Create"/> or <see cref="Seed"/> has run, so a test can build its own without
        /// touching a global.
        /// </summary>
        public static CampaignNotes Current { get; private set; }

        /// <summary>How many notes the campaign holds.</summary>
        public int Count => _ids.Count;

        /// <summary>The note ids, in the order they were added. Live — do not mutate.</summary>
        public IReadOnlyList<string> Ids => _ids;

        /// <summary>Whether a note is already in the journal.</summary>
        public bool Contains(string noteId) =>
            !string.IsNullOrEmpty(noteId) && _ids.Contains(noteId);

        /// <summary>
        /// The port of <c>Game.addNote</c> (<c>Game.as:666-673</c>). Returns false when the id was
        /// already present, so a caller can tell "added" from "already knew".
        /// </summary>
        public bool Add(string noteId)
        {
            if (string.IsNullOrEmpty(noteId)) return false;
            if (_ids.Contains(noteId)) return false;

            _ids.Add(noteId);
            return true;
        }

        /// <summary>Empties the journal. Used by a new game and by the fixtures.</summary>
        public void Clear() => _ids.Clear();

        /// <summary>Replaces the contents with the three seeds — <c>Game.as:301-303</c>.</summary>
        public void SeedDefaults()
        {
            _ids.Clear();
            for (int i = 0; i < SeedIds.Length; i++) Add(SeedIds[i]);
        }

        /// <summary>Builds a store seeded with the three default notes and installs it as
        /// <see cref="Current"/>. The port's "new game" entry point.</summary>
        public static CampaignNotes Create()
        {
            var notes = new CampaignNotes();
            notes.SeedDefaults();
            Current = notes;
            return notes;
        }

        /// <summary>
        /// Installs a store, replacing any previous one. Used by the campaign boot path; a caller with
        /// no new-game notion passes <c>seed: true</c> to get the three defaults.
        /// </summary>
        public static CampaignNotes Seed(bool seed = true)
        {
            var notes = new CampaignNotes();
            if (seed) notes.SeedDefaults();
            Current = notes;
            return notes;
        }

        /// <summary>Clears the process-wide store. For tests and for a full reset.</summary>
        public static void ResetCurrent() => Current = null;
    }
}
