using System;
using System.Collections.Generic;
using System.IO;

namespace PFE.Systems.Map.Serialization
{
    /// <summary>
    /// One file in the save directory, reduced to the three numbers a "has the list changed?" test can
    /// be made of.
    ///
    /// <para><b>Why three numbers and not a directory listing.</b> The question a caller actually has is
    /// <i>"do I need to decompress everything again?"</i> — see <see cref="SaveListScan.IsStale"/>. A
    /// listing answers it but is not comparable, cacheable or printable; three scalars are all three,
    /// and they cost one <c>FileInfo</c> per file instead of a full read.</para>
    /// </summary>
    public readonly struct SaveFileFact : IEquatable<SaveFileFact>
    {
        /// <summary>Full path. Carried for diagnostics only — it is <b>not</b> part of the stamp.</summary>
        public readonly string Path;

        /// <summary>File size in bytes.</summary>
        public readonly long Length;

        /// <summary><c>FileInfo.LastWriteTimeUtc.Ticks</c>.</summary>
        public readonly long LastWriteUtcTicks;

        public SaveFileFact(string path, long length, long lastWriteUtcTicks)
        {
            Path = path ?? string.Empty;
            Length = length;
            LastWriteUtcTicks = lastWriteUtcTicks;
        }

        public bool Equals(SaveFileFact other)
        {
            return string.Equals(Path, other.Path, StringComparison.Ordinal) &&
                   Length == other.Length &&
                   LastWriteUtcTicks == other.LastWriteUtcTicks;
        }

        public override bool Equals(object obj) => obj is SaveFileFact other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Path != null ? Path.GetHashCode() : 0;
                hash = (hash * 397) ^ Length.GetHashCode();
                hash = (hash * 397) ^ LastWriteUtcTicks.GetHashCode();
                return hash;
            }
        }

        public override string ToString()
        {
            return $"{Path} {Length}B @{LastWriteUtcTicks}";
        }
    }

    /// <summary>
    /// A cheap fingerprint of the save directory — the answer to <i>"has anything on disk changed since
    /// I last built the save list?"</i>
    ///
    /// <para><b>What it is for.</b> <c>SaveManager.GetAllSaves</c> is <b>not</b> a cheap call despite
    /// its name: for every file it gunzips the whole save and runs
    /// <c>JsonUtility.FromJson&lt;WorldSaveData&gt;</c> — the entire world, every room's every tile —
    /// in order to return six scalars. Measured on a real save directory that is ~20 MB of JSON and
    /// 24,000 tiles <i>per file</i>, i.e. ~150 ms per save. A debug panel that calls it from
    /// <c>OnGUI</c> runs it at least twice per frame (IMGUI's Layout and Repaint passes) plus once per
    /// mouse-move, which is the whole of the reported "3 fps on the Save / Load tab".</para>
    ///
    /// <para><b>What it deliberately is not.</b> It is not a content hash: it costs one
    /// <c>FileInfo</c> per file, not one read per file, so it cannot tell a rewritten file of the same
    /// size and timestamp from an untouched one. That is the honest trade — catching that case would
    /// cost the read this type exists to avoid — and it is why callers that <i>know</i> they changed
    /// the directory invalidate explicitly rather than trusting the stamp.</para>
    /// </summary>
    public readonly struct SaveDirectoryStamp : IEquatable<SaveDirectoryStamp>
    {
        /// <summary>How many save files the directory holds.</summary>
        public readonly int FileCount;

        /// <summary>Sum of their sizes. Catches a rewritten file that kept its timestamp.</summary>
        public readonly long TotalBytes;

        /// <summary>The newest <c>LastWriteTimeUtc</c>. Catches a rewrite that kept its size.</summary>
        public readonly long NewestWriteUtcTicks;

        public SaveDirectoryStamp(int fileCount, long totalBytes, long newestWriteUtcTicks)
        {
            FileCount = fileCount;
            TotalBytes = totalBytes;
            NewestWriteUtcTicks = newestWriteUtcTicks;
        }

        /// <summary>
        /// The stamp of a directory with nothing in it. Equal to <c>default</c> on purpose: a fresh
        /// cache field is <c>default</c>, so "never scanned" and "scanned, and there were no saves"
        /// must not be distinguishable, or an empty directory would rescan forever.
        /// </summary>
        public static SaveDirectoryStamp None => default;

        public bool Equals(SaveDirectoryStamp other)
        {
            return FileCount == other.FileCount &&
                   TotalBytes == other.TotalBytes &&
                   NewestWriteUtcTicks == other.NewestWriteUtcTicks;
        }

        public override bool Equals(object obj) => obj is SaveDirectoryStamp other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = FileCount;
                hash = (hash * 397) ^ TotalBytes.GetHashCode();
                hash = (hash * 397) ^ NewestWriteUtcTicks.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(SaveDirectoryStamp a, SaveDirectoryStamp b) => a.Equals(b);
        public static bool operator !=(SaveDirectoryStamp a, SaveDirectoryStamp b) => !a.Equals(b);

        public override string ToString()
        {
            return $"{FileCount} file(s), {TotalBytes} B, newest @{NewestWriteUtcTicks}";
        }
    }

    /// <summary>
    /// Reads and compares <see cref="SaveDirectoryStamp"/>s.
    ///
    /// <para><b>Split from the panel on purpose.</b> Everything here except <see cref="Read"/> is pure
    /// arithmetic over a list, so the "is the cache still valid" rule is testable without a Unity
    /// runtime, a save directory or an <c>Application.persistentDataPath</c> (an ECall that throws in
    /// this project's offline harness). The panel keeps only the one impure line.</para>
    /// </summary>
    public static class SaveListScan
    {
        /// <summary>
        /// Whether a cached list built under <paramref name="cached"/> may still be served.
        ///
        /// <para><b>Any difference counts.</b> A file added or deleted changes the count; a rewrite
        /// changes the size or the timestamp. Comparing only one of the three would let the other two
        /// cases serve a stale list, and a stale list on this panel means a save you just wrote is
        /// missing from the row you are about to click.</para>
        /// </summary>
        public static bool IsStale(SaveDirectoryStamp cached, SaveDirectoryStamp current)
        {
            return cached != current;
        }

        /// <summary>
        /// Folds file facts into a stamp. <b>Pure</b> — no disk, no clock, no Unity.
        ///
        /// <para>An empty or null input yields <see cref="SaveDirectoryStamp.None"/>, which is what
        /// makes "no saves" and "no directory" the same answer instead of two states a caller has to
        /// branch on.</para>
        /// </summary>
        public static SaveDirectoryStamp Stamp(IEnumerable<SaveFileFact> files)
        {
            if (files == null) return SaveDirectoryStamp.None;

            int count = 0;
            long total = 0;
            long newest = 0;

            foreach (SaveFileFact file in files)
            {
                count++;
                total += file.Length;
                if (file.LastWriteUtcTicks > newest) newest = file.LastWriteUtcTicks;
            }

            return new SaveDirectoryStamp(count, total, newest);
        }

        /// <summary>
        /// The stamp of the save directory right now — <b>the only impure member of this type</b>.
        ///
        /// <para><b>It asks <see cref="WorldSerializer"/> for the files rather than re-deriving the
        /// pattern.</b> <c>GetAllSaveFiles</c> already owns <c>save_*.json.gz</c> and the directory it
        /// lives in; a second copy of either literal here is exactly the drift this project keeps
        /// re-learning, and a second copy would also silently disagree about what counts as a save the
        /// day a sidecar or an uncompressed export is added to that directory.</para>
        ///
        /// <para><b>A missing or unreadable directory is <see cref="SaveDirectoryStamp.None"/>, not an
        /// exception.</b> This runs on a debug panel, i.e. at the moment the developer is most likely
        /// to be looking at a broken save path, and a stamp it can display beats a panel that throws
        /// inside <c>OnGUI</c> and stops drawing.</para>
        /// </summary>
        public static SaveDirectoryStamp ReadCurrent()
        {
            try
            {
                string[] files = WorldSerializer.GetAllSaveFiles();
                if (files == null || files.Length == 0) return SaveDirectoryStamp.None;

                var facts = new List<SaveFileFact>(files.Length);
                foreach (string path in files)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    facts.Add(FactOf(path));
                }

                return Stamp(facts);
            }
            catch (Exception)
            {
                // A stamp is an optimisation; the caller still has the authoritative (slow) path.
                // Failing to None forces that path rather than hiding an unreadable directory behind a
                // "nothing changed" that would serve a stale list indefinitely.
                return SaveDirectoryStamp.None;
            }
        }

        private static SaveFileFact FactOf(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return new SaveFileFact(path, info.Length, info.LastWriteTimeUtc.Ticks);
            }
            catch (Exception)
            {
                // Length 0 / ticks 0 still CHANGES the stamp if the file is the only one, which is the
                // safe direction: an unreadable file forces a rescan rather than freezing the list.
                return new SaveFileFact(path, 0L, 0L);
            }
        }
    }
}
