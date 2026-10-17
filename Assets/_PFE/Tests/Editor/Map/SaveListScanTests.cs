using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Map.Serialization;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// The "do I still need to decompress everything?" rule — the pure half of the Save / Load tab's
    /// cache.
    ///
    /// <para><b>What this exists to make unrepeatable.</b> <c>SaveManager.GetAllSaves</c> is not a cheap
    /// call despite its name: for every save it gunzips the whole file and runs
    /// <c>JsonUtility.FromJson&lt;WorldSaveData&gt;</c> — the entire world, every room's every tile —
    /// in order to return six scalars. Measured on a real save directory that is ~20 MB of JSON and
    /// 24,000 tiles <i>per file</i>. The panel calls it from <c>OnGUI</c>, which IMGUI runs at least
    /// twice per frame plus once per mouse-move, so the un-cached version cost several hundred
    /// milliseconds <i>per frame</i>: the reported 3 fps. The cache, and therefore this rule, is the
    /// fix.</para>
    ///
    /// <para><b>Why a purity test rather than a behavioural one.</b> The rule is arithmetic over a list
    /// of numbers — three of them. Driving it through the real path means writing a real 20 MB
    /// world save to <c>persistentDataPath</c>, and the fixture would then be asserting the Unity file
    /// system rather than the comparison. Here the directory is three numbers, so every branch is
    /// reachable and every assertion means what it says.</para>
    /// </summary>
    [TestFixture]
    public sealed class SaveListScanTests
    {
        private static SaveFileFact Fact(string path, long length, long ticks)
        {
            return new SaveFileFact(path, length, ticks);
        }

        // ── Stamp ──────────────────────────────────────────────────────────────────────────────

        [Test]
        public void Stamp_OfNoFiles_IsTheNoneStamp()
        {
            var none = SaveListScan.Stamp(new List<SaveFileFact>());

            Assert.That(none, Is.EqualTo(SaveDirectoryStamp.None),
                "An empty directory must produce the same stamp as 'never scanned'. Making them " +
                "different would force every caller to special-case an empty directory, and the " +
                "special case is where a never-scanned cache gets re-scanned forever.");
        }

        [Test]
        public void Stamp_OfNull_IsTheNoneStamp()
        {
            Assert.That(SaveListScan.Stamp(null), Is.EqualTo(SaveDirectoryStamp.None),
                "A caller with a failed enumeration must fail towards 'nothing here', which forces " +
                "the slow path — not towards a fake stamp that would freeze the cache.");
        }

        [Test]
        public void Stamp_CountsFiles_SumsBytes_TakesTheNewestWrite()
        {
            List<SaveFileFact> files = new List<SaveFileFact>
            {
                Fact("/a/save_1.json.gz", 100L, 500L),
                Fact("/a/save_2.json.gz", 250L, 900L),   // newest, largest
                Fact("/a/save_3.json.gz", 40L,  700L),
            };

            var stamp = SaveListScan.Stamp(files);

            Assert.That(stamp.FileCount, Is.EqualTo(3), "Every file counts, including the smallest.");
            Assert.That(stamp.TotalBytes, Is.EqualTo(390L), "Sizes are summed, not replaced.");
            Assert.That(stamp.NewestWriteUtcTicks, Is.EqualTo(900L),
                "The newest write time is kept — that is the field which notices a rewrite that " +
                "kept its size.");
        }

        [Test]
        public void Stamp_DoesNotDependOnOrder()
        {
            List<SaveFileFact> a = new List<SaveFileFact>
            {
                Fact("/a/save_1.json.gz", 100L, 500L),
                Fact("/a/save_2.json.gz", 250L, 900L),
            };
            List<SaveFileFact> b = new List<SaveFileFact>
            {
                Fact("/a/save_2.json.gz", 250L, 900L),
                Fact("/a/save_1.json.gz", 100L, 500L),
            };

            Assert.That(SaveListScan.Stamp(a), Is.EqualTo(SaveListScan.Stamp(b)),
                "File order is not part of the stamp. If it were, a directory listing returned in a " +
                "different order — which some filesystems do — would rescan every time.");
        }

        [Test]
        public void Stamp_OfASingleFile_IsNeverTheNoneStamp()
        {
            var one = SaveListScan.Stamp(new List<SaveFileFact> { Fact("/a/save_x.json.gz", 0L, 0L) });

            Assert.That(one, Is.Not.EqualTo(SaveDirectoryStamp.None),
                "A directory holding one unreadable (0-byte, 0-tick) file must not produce the " +
                "same stamp as an empty directory, or an unreadable save would make the cache " +
                "believe nothing had ever been written.");
        }

        // ── IsStale ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void IsStale_WhenNothingChanged_False()
        {
            List<SaveFileFact> files = new List<SaveFileFact>
            {
                Fact("/a/save_1.json.gz", 100L, 500L),
                Fact("/a/save_2.json.gz", 250L, 900L),
            };

            var stamp = SaveListScan.Stamp(files);

            Assert.That(SaveListScan.IsStale(stamp, stamp), Is.False,
                "This is the whole point: an unchanged directory must serve the cache.");
        }

        [Test]
        public void IsStale_WhenASaveIsAdded_True()
        {
            var before = SaveListScan.Stamp(new List<SaveFileFact> { Fact("/a/save_1.json.gz", 100L, 500L) });
            var after = SaveListScan.Stamp(new List<SaveFileFact>
            {
                Fact("/a/save_1.json.gz", 100L, 500L),
                Fact("/a/save_2.json.gz", 250L, 900L),
            });

            Assert.That(SaveListScan.IsStale(before, after), Is.True,
                "A save written this session must appear in the list without a manual refresh.");
        }

        [Test]
        public void IsStale_WhenASaveIsDeleted_True()
        {
            var before = SaveListScan.Stamp(new List<SaveFileFact>
            {
                Fact("/a/save_1.json.gz", 100L, 500L),
                Fact("/a/save_2.json.gz", 250L, 900L),
            });
            var after = SaveListScan.Stamp(new List<SaveFileFact> { Fact("/a/save_1.json.gz", 100L, 500L) });

            Assert.That(SaveListScan.IsStale(before, after), Is.True,
                "A deleted save must disappear from the list, not stay a row that fails when clicked.");
        }

        [Test]
        public void IsStale_WhenASaveIsRewritten_True()
        {
            var before = SaveListScan.Stamp(new List<SaveFileFact> { Fact("/a/save_1.json.gz", 100L, 500L) });

            var sameSizeNewer = SaveListScan.Stamp(new List<SaveFileFact> { Fact("/a/save_1.json.gz", 100L, 501L) });
            var newerSize = SaveListScan.Stamp(new List<SaveFileFact> { Fact("/a/save_1.json.gz", 120L, 500L) });

            Assert.That(SaveListScan.IsStale(before, sameSizeNewer), Is.True,
                "A rewrite that kept its size is caught by the timestamp.");
            Assert.That(SaveListScan.IsStale(before, newerSize), Is.True,
                "A rewrite that kept its timestamp is caught by the size. Only one of the two " +
                "being compared would leave the other case serving a stale list.");
        }

        [Test]
        public void IsStale_WhenTheLastSaveWasDeleted_True()
        {
            // The case that forces the explicit InvalidateSaveList() on the panel: with a single save,
            // "the directory" after deleting it and "the directory never had any saves" are the same
            // stamp, so the stamp ALONE cannot see this one.
            var before = SaveListScan.Stamp(new List<SaveFileFact> { Fact("/a/save_1.json.gz", 100L, 500L) });

            Assert.That(SaveListScan.IsStale(before, SaveDirectoryStamp.None), Is.True,
                "Going from 'one save' to 'nothing' must read as stale.");
            Assert.That(SaveListScan.IsStale(SaveDirectoryStamp.None, SaveDirectoryStamp.None), Is.False,
                "Going from 'nothing' to 'nothing' must NOT read as stale — this is precisely the case " +
                "the panel closes with InvalidateSaveList() instead of the stamp.");
        }

        // ── The value type ─────────────────────────────────────────────────────────────────────

        [Test]
        public void Stamp_EquatesByValue_NotByReference()
        {
            var a = SaveListScan.Stamp(new List<SaveFileFact> { Fact("/a/save_1.json.gz", 100L, 500L) });
            var b = SaveListScan.Stamp(new List<SaveFileFact> { Fact("/a/save_1.json.gz", 100L, 500L) });

            Assert.That(a, Is.EqualTo(b), "Two stamps over the same numbers must be Equal.");
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()),
                "Equal stamps must hash alike or they cannot be used as a dictionary key.");
            Assert.That(a == b, Is.True);
            Assert.That(a != b, Is.False);
            Assert.That(a.Equals(null), Is.False, "Equals(null) must be false, not an exception.");
            Assert.That(a.Equals((object)b), Is.True, "The object overload must route to the typed one.");
        }

        [Test]
        public void Stamp_PathIsNotPartOfTheComparison()
        {
            // The path is carried for diagnostics only. A save moved between calls by a caller that
            // renamed its variable must not invalidate the cache.
            var a = SaveListScan.Stamp(new List<SaveFileFact> { Fact("/a/save_1.json.gz", 100L, 500L) });
            var b = SaveListScan.Stamp(new List<SaveFileFact> { Fact("/b/save_1.json.gz", 100L, 500L) });

            Assert.That(a, Is.EqualTo(b),
                "The stamp is about the directory's contents, not about what the caller happened " +
                "to name the paths.");
        }

        [Test]
        public void Stamp_ToString_NamesItsThreeNumbers()
        {
            var stamp = SaveListScan.Stamp(new List<SaveFileFact>
            {
                Fact("/a/save_1.json.gz", 100L, 500L),
                Fact("/a/save_2.json.gz", 250L, 900L),
            });

            Assert.That(stamp.ToString(), Is.EqualTo("2 file(s), 350 B, newest @900"),
                "This string is what the panel can print when a tester asks 'why did it rescan?'.");
        }
    }
}
