using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Reads the XML literal embedded in the AS3 <c>AllData.as</c> source.
    ///
    /// <para><b>Why this is a runtime type.</b> The same reason <see cref="WeaponVisSymbolRule"/> is:
    /// the editor assembly is not referenced by the offline test wall, so anything living there is
    /// invisible to it. This extraction used to live in <c>WeaponSpriteImporter</c>, and it searched
    /// for the wrong root element — <c>&lt;alldata&gt;</c>, which does not occur in the file even once,
    /// instead of <c>&lt;all&gt;</c>. It therefore returned an empty weapon-id set on every run. That
    /// was harmless while the symbol filter was a hand-maintained blocklist that never consulted
    /// AllData.as, and became fatal the moment the filter started classifying from the weapon-id set:
    /// the filter rejected every symbol, the import created nothing, and all 213 weapons were reported
    /// as "No visual def". One cause, 213 costumes — and no test could see any of it.</para>
    ///
    /// <para><b>What the file actually looks like.</b> <c>AllData.as</c> is ActionScript wrapping an XML
    /// literal: <c>public static var d:XML = &lt;all&gt; … &lt;/all&gt;;</c>. The root element is
    /// <c>&lt;all&gt;</c>, occurring exactly once, as does its closing tag (byte 86 and byte 360717 in
    /// the current revision). Every other importer in this project already reads it that way —
    /// <c>SoundImporter</c>, <c>DataConverterWindow</c> and <c>XMLConverter</c> all search for
    /// <c>&lt;all&gt;</c>. This one was the odd one out.</para>
    /// </summary>
    public static class AllDataXml
    {
        /// <summary>The root element of the embedded XML literal. Note: not <c>&lt;alldata&gt;</c>.</summary>
        public const string RootOpen = "<all>";

        /// <summary>Closing tag matching <see cref="RootOpen"/>.</summary>
        public const string RootClose = "</all>";

        /// <summary>Why <see cref="TryReadWeaponIds"/> could not produce a set of ids.</summary>
        public enum Outcome
        {
            /// <summary>The block was found and parsed.</summary>
            Ok,

            /// <summary>No <c>&lt;all&gt; … &lt;/all&gt;</c> literal is present.</summary>
            MissingBlock,

            /// <summary>The block is present but is not well-formed XML.</summary>
            ParseFailed,
        }

        /// <summary>
        /// Extracts the <c>&lt;all&gt; … &lt;/all&gt;</c> literal from the file text, or <c>null</c>
        /// when it is absent or malformed.
        /// </summary>
        public static string ExtractRootBlock(string allDataText)
        {
            if (string.IsNullOrEmpty(allDataText)) return null;

            int start = allDataText.IndexOf(RootOpen, StringComparison.Ordinal);
            if (start < 0) return null;

            int end = allDataText.LastIndexOf(RootClose, StringComparison.Ordinal);
            if (end < start) return null;   // a closing tag before the opening one is not a block

            return allDataText.Substring(start, end - start + RootClose.Length);
        }

        /// <summary>
        /// Finds and parses the embedded literal in one step.
        ///
        /// <para>The parsed <paramref name="root"/> is returned as well as the id set, because the
        /// caller also needs the <c>&lt;vis&gt;</c> children; parsing twice to get both would be waste,
        /// and re-implementing the parse at the call site is how the tag drifted in the first place.</para>
        ///
        /// <para>The outcome is returned rather than inferred from the id set being empty, because
        /// "no block" and "a block with no weapons" are different problems and the caller has to be
        /// able to say which. The original code could not, and reported both as silence.</para>
        /// </summary>
        public static Outcome TryRead(string allDataText, out XElement root, out HashSet<string> weaponIds, out string error)
        {
            root = null;
            weaponIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            error = null;

            string block = ExtractRootBlock(allDataText);
            if (block == null)
            {
                error = $"no {RootOpen}...{RootClose} block";
                return Outcome.MissingBlock;
            }

            try
            {
                root = XElement.Parse(block);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return Outcome.ParseFailed;
            }

            foreach (XElement weapon in root.Descendants("weapon"))
            {
                string id = (string)weapon.Attribute("id");
                if (!string.IsNullOrEmpty(id)) weaponIds.Add(id);
            }

            return Outcome.Ok;
        }
    }
}
