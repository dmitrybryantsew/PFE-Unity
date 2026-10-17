using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Scripting
{
    /// <summary>
    /// Pins the Save / Load tab's cache: <c>SaveManager.GetAllSaves</c> may be called <b>once</b> in the
    /// overlay, and that once must sit behind the stamp comparison.
    ///
    /// <para><b>The bug this exists to make unrepeatable.</b> <c>GetAllSaves</c> reads every save in
    /// full — gunzip, then <c>JsonUtility.FromJson&lt;WorldSaveData&gt;</c> over ~20 MB of JSON and
    /// 24,000 tile entries — to return six scalars. The tab called it straight from
    /// <c>DrawSaveLoadAllSaves</c>, i.e. from <c>OnGUI</c>, which IMGUI runs at least twice per frame
    /// (Layout and Repaint) plus once per mouse-move. Result: several hundred milliseconds of main
    /// thread per frame, ~3 fps, on one tab only — and nothing red, because a debug panel drawing
    /// slowly is not a failed assertion anywhere. The Land Map tab next door already carries its own
    /// signature cache and Refresh button for exactly this reason; this is the same guard for the
    /// tab that was missing it.</para>
    ///
    /// <para><b>Why a lint rather than a behavioural test.</b> Proving the fix behaviourally needs a
    /// MonoBehaviour's <c>OnGUI</c> loop, a <c>SaveManager</c> and a 20 MB save file on disk — none of
    /// which this project's harness can construct. The invariant, though, is about <i>where the call
    /// sits</i>, and that is readable.</para>
    /// </summary>
    [TestFixture]
    public sealed class SaveLoadTabCacheLintTests
    {
        private const string OverlayPath = "Core/Scripting/PlayerDebugEditorOverlay.cs";

        [Test]
        public void TheTab_DrawDoesNotReadTheSaveDirectory()
        {
            string overlay = SourceLint.ReadStripped(OverlayPath);

            string draw = SourceLint.MethodBody(overlay, "private void DrawSaveLoadAllSaves(SaveManager save)");

            Assert.That(draw, Does.Not.Contain("GetAllSaves"),
                "DrawSaveLoadAllSaves calls the save list directly again. This is the 3 fps: " +
                "GetAllSaves decompresses and deserialises the entire world for six scalars, and " +
                "OnGUI runs more than once per frame.");
        }

        [Test]
        public void TheTab_DrawsThroughTheCachedAccessor()
        {
            string overlay = SourceLint.ReadStripped(OverlayPath);

            string draw = SourceLint.MethodBody(overlay, "private void DrawSaveLoadAllSaves(SaveManager save)");

            Assert.That(draw, Does.Contain("GetSaveList"),
                "The tab no longer goes through the cached accessor, so the cache can no longer " +
                "prevent the rescan. Go back through GetSaveList rather than re-adding the call.");
        }

        [Test]
        public void TheCachedAccessor_OwnsTheOnlyDiskRead_AndGuardsItWithTheStamp()
        {
            string overlay = SourceLint.ReadStripped(OverlayPath);

            string accessor = SourceLint.MethodBody(
                overlay, "private List<SaveMetadata> GetSaveList(SaveManager save, bool force = false)");

            Assert.That(accessor, Does.Contain("GetAllSaves"),
                "The accessor must be where the expensive read still lives. If it moved out of the " +
                "accessor, the stamp below guards nothing.");
            Assert.That(accessor, Does.Contain("SaveListScan"),
                "The accessor must compare a directory stamp before serving the cache. Without the " +
                "comparison the list is either never rebuilt (a save you just wrote is missing) or " +
                "always rebuilt (the 3 fps, unchanged).");
        }

        [Test]
        public void TheOverlay_CallsGetAllSavesExactlyOnce()
        {
            string overlay = SourceLint.ReadStripped(OverlayPath);

            int occurrences = CountOf(overlay, "GetAllSaves");

            Assert.That(occurrences, Is.EqualTo(1),
                "The overlay makes " + occurrences + " calls to GetAllSaves(). Exactly one is " +
                "allowed — the one inside the cached accessor. This is the regression guard: any " +
                "second call site reintroduces a per-frame full deserialisation, and the panel's " +
                "frame cost goes back to hundreds of milliseconds.");
        }

        [Test]
        public void TheMutationButtons_InvalidateTheCache()
        {
            string overlay = SourceLint.ReadStripped(OverlayPath);

            int invalidations = CountOf(overlay, "InvalidateSaveList");

            // Quick Save, Quick Load, Checkpoint Save, Trigger now, and the per-row Load and Delete
            // buttons = six. `>= 4` and not `== 6` so that adding a button does not fail a lint about
            // a rule that is still being followed, and removing one below four still does.
            Assert.That(invalidations, Is.GreaterThanOrEqualTo(4),
                "Only " + invalidations + " buttons invalidate the cache. A mutation that leaves the " +
                "cache alone is only correct if the stamp will move — and it will not when the change " +
                "keeps the file size (a checkpoint save overwriting a same-size autosave) or deletes " +
                "the last file (whose new stamp is None, identical to 'never had any saves').");
        }

        /// <summary>Occurrences of a literal, overlapping none — plain counting, which is all the
        /// "exactly one call site" guard needs.</summary>
        private static int CountOf(string haystack, string needle)
        {
            int count = 0;
            int index = haystack.IndexOf(needle, System.StringComparison.Ordinal);
            while (index >= 0)
            {
                count++;
                index = haystack.IndexOf(needle, index + needle.Length, System.StringComparison.Ordinal);
            }
            return count;
        }
    }
}
