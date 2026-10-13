using PFE.UI.PipBuck;
using UnityEngine;

namespace PFE.UI.Common
{
    /// <summary>
    /// The one owner of the parsed localisation document — <c>Resources/Localisation/text_en.xml</c>,
    /// the file the Oracle's <c>Res.d</c>/<c>Res.e</c> hold (<c>Res.as:55-147</c>).
    ///
    /// <para><b>Why a shared provider rather than a load at each use.</b> Two subsystems now need this
    /// table and they are not related: the PipBuck resolves its captions through <c>Res.pipText</c>, and
    /// the script layer resolves <c>mess</c>/<c>dial</c> through <c>Res.messText</c> and
    /// <c>GUI.dialText</c>. Each load parses the whole 800 KB document, so two loaders would parse it
    /// twice, at two moments, and could disagree about whether it was found. Caching it here means the
    /// answer to "is the Oracle's text available" is the same for both, and the warning is emitted
    /// once.</para>
    ///
    /// <para><b>Lazy, and that is deliberate.</b> The document is needed the first time a caption is
    /// asked for, not when the container is built — a test scene that never opens the PipBuck should not
    /// pay for it.</para>
    /// </summary>
    public static class PfeTextTableProvider
    {
        /// <summary>Where the shipping build keeps <c>lang.xml</c>'s <c>en</c> document, as a Unity
        /// resource path.</summary>
        public const string TextTableResourcePath = "Localisation/text_en";

        private static PipTextTable _shared;

        /// <summary>
        /// The parsed document, or <see cref="PipTextTable.Empty"/> when it is absent or unreadable.
        /// Never null — every lookup then misses, which is the honest answer and keeps callers free of
        /// null checks.
        /// </summary>
        public static PipTextTable Shared => _shared ??= Load();

        /// <summary>
        /// Drops the cache. For a test that swaps the document under the provider, and for a
        /// domain-reload-sensitive editor path.
        /// </summary>
        public static void Reset() => _shared = null;

        /// <summary>
        /// Reads and parses the document.
        ///
        /// <para><b>Why a missing document is a warning and not an error.</b> Every caption has a
        /// documented fallback, so the game is still readable without it — it just reads the port's own
        /// words rather than the Oracle's. Failing the build over a caption table would take the
        /// inventory and the campaign's messages down with it.</para>
        ///
        /// <para><b>Why a parse failure is reported rather than swallowed.</b> A malformed document that
        /// silently became <see cref="PipTextTable.Empty"/> would look exactly like a missing file, and
        /// the two have different fixes.</para>
        /// </summary>
        public static PipTextTable Load()
        {
            TextAsset asset = Resources.Load<TextAsset>(TextTableResourcePath);

            if (asset == null)
            {
                Debug.LogWarning("[PfeTextTableProvider] No localisation document at Resources/" +
                                 TextTableResourcePath + ".xml. PipBuck captions and every campaign " +
                                 "message fall back to the port's own English, which is readable but " +
                                 "is not the Oracle's text. Copy text_en.xml from the shipping build " +
                                 "into Assets/_PFE/Data/Resources/Localisation/.");
                return PipTextTable.Empty;
            }

            try
            {
                PipTextTable table = PipTextTable.Parse(asset.text);
                if (table.IsEmpty)
                {
                    Debug.LogError("[PfeTextTableProvider] The localisation document at Resources/" +
                                   TextTableResourcePath + ".xml parsed to zero entries. Every caption " +
                                   "will use its fallback. The file is probably truncated or is not " +
                                   "the Oracle's <all>-rooted document.");
                }

                return table;
            }
            catch (System.Exception exception)
            {
                Debug.LogError("[PfeTextTableProvider] The localisation document at Resources/" +
                               TextTableResourcePath + ".xml failed to parse: " + exception.Message);
                return PipTextTable.Empty;
            }
        }
    }
}
