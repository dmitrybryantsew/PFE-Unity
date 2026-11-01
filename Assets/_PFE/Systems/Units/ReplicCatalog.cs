using System;
using System.Xml.Linq;
using PFE.Data.Definitions;
using PFE.Sim.Units;
using PFE.Systems.Loot;
using UnityEngine;

namespace PFE.Systems.Units
{
    /// <summary>
    /// The runtime half of enemy speech: the parsed <c>&lt;replic&gt;</c> table plus the
    /// <b>unit id → voice set</b> map that AS3 keeps on <c>Unit.id_replic</c>.
    ///
    /// <para><b>Two documents, and neither is the unit asset.</b> The lines live in
    /// <c>Resources/Localisation/text_en.xml</c>; the mapping lives on each unit row's
    /// <c>&lt;vis replic='…'&gt;</c> in <c>Resources/Loot/AllData</c>. The imported
    /// <c>UnitDefinition</c> assets carry neither — the importer that would fill a <c>replicaId</c>
    /// field is owner-only here, and the same reasoning as <c>ItemPriceRules</c> applies: read the
    /// shipped row at runtime, do not wait for an import that cannot be run.</para>
    ///
    /// <para><b>The inheritance rule lives in <see cref="ReplicSpeakerMap"/>, not here.</b> This type
    /// only reads the documents and hands the rows over; the walk up <c>@parent</c> is Unity-free and
    /// fixture-pinned, because that is the half with the trap in it (a variant row has no
    /// <c>&lt;vis&gt;</c> of its own).</para>
    /// </summary>
    public sealed class ReplicCatalog
    {
        /// <summary>
        /// The localisation document, as a Unity resource path — the same file
        /// <c>UnitNameTable.TextTableResourcePath</c> and <c>PipTextTable</c> read.
        /// </summary>
        public const string TextTableResourcePath = "Localisation/text_en";

        private readonly ReplicTextTable _text;
        private readonly ReplicSpeakerMap _speakers;

        /// <summary>An empty catalog — every lookup misses. Never null, so callers need no null checks.</summary>
        public static ReplicCatalog Empty { get; } = new ReplicCatalog(ReplicTextTable.Empty, ReplicSpeakerMap.Empty);

        private ReplicCatalog(ReplicTextTable text, ReplicSpeakerMap speakers)
        {
            _text = text;
            _speakers = speakers;
        }

        private static ReplicCatalog _shared;

        /// <summary>
        /// The session's catalog, built on first use from the two shipped documents.
        ///
        /// <para>A missing document yields <see cref="Empty"/> and one warning, not an exception:
        /// silent enemies are a cosmetic failure and must not take a spawn down. That is the same
        /// contract <c>UnitNameTable.Shared</c> has, for the same reason.</para>
        /// </summary>
        public static ReplicCatalog Shared
        {
            get
            {
                if (_shared != null) return _shared;

                string textXml = LoadResource(TextTableResourcePath, "enemy speech lines");
                string allData = LoadResource(LootRuntime.AllDataResourcePath, "the unit → voice-set map");

                if (textXml == null)
                {
                    _shared = Empty;
                    return _shared;
                }

                _shared = Parse(textXml, allData);
                return _shared;
            }
        }

        /// <summary>Drop the session's catalog, so the next <see cref="Shared"/> re-parses.</summary>
        public static void Reset() => _shared = null;

        /// <summary>
        /// AS3 <c>World.w.pers.persName</c> — substituted for <c>@lp</c> in a line
        /// (<c>Res.as:310</c>).
        ///
        /// <para><b>Static and settable, because the oracle's is.</b> It is one world-level value, and
        /// ten of the shipped lines use it (<c>"Oh, @lp, long time no see!"</c>). The default is the
        /// protagonist's name; the PipBuck's own resolution
        /// (<c>PlayerPipInfoSource.ResolvePlayerName</c>) can overwrite it once a save is loaded. A
        /// speaker whose name is empty simply leaves <c>@lp</c> in the text, which is the oracle's
        /// behaviour for an unset <c>persName</c>.</para>
        /// </summary>
        public static string PlayerName { get; set; } = "Littlepip";

        /// <summary>The parsed speech table — exposed so a debug readout can count it.</summary>
        public ReplicTextTable Text => _text;

        /// <summary>The unit → voice-set map, for callers that need the raw answer.</summary>
        public ReplicSpeakerMap Speakers => _speakers;

        /// <summary>
        /// The voice set for <paramref name="unitId"/>, or <c>null</c> when the unit row (or any of its
        /// ancestors) names none.
        /// </summary>
        public string SpeakerFor(string unitId) => _speakers.SpeakerFor(unitId);

        /// <summary>
        /// Resolve one bark for a unit — the map lookup, the rules and the text in one call.
        /// </summary>
        /// <param name="unitId">The unit's definition id, e.g. <c>raider3</c>.</param>
        /// <param name="kind">The act.</param>
        /// <param name="draw01">Draw for the line index.</param>
        /// <param name="male">AS3 <c>msex</c> — true unless the unit is female.</param>
        /// <param name="matureFilter">AS3 <c>matFilter</c> — true means censored.</param>
        /// <param name="playerName">Substituted for <c>@lp</c>.</param>
        /// <param name="text">The prepared line, or <c>null</c>.</param>
        public bool TryResolve(
            string unitId,
            ReplicKind kind,
            float draw01,
            bool male,
            bool matureFilter,
            string playerName,
            out string text)
        {
            text = null;
            string speaker = SpeakerFor(unitId);
            if (speaker == null) return false;

            return ReplicRules.TryResolveText(_text, speaker, kind, draw01, male, matureFilter, playerName, out text);
        }

        /// <summary>
        /// Build a catalog from the two documents as text — the fixture entry point, and what
        /// <see cref="Shared"/> calls.
        /// </summary>
        /// <param name="textXml">The localisation document.</param>
        /// <param name="allDataText">The <c>AllData</c> source; <c>null</c> yields a text-only catalog.</param>
        public static ReplicCatalog Parse(string textXml, string allDataText)
        {
            ReplicTextTable table = ReplicTextTable.Parse(textXml);
            ReplicSpeakerMap speakers = ReplicSpeakerMap.Empty;

            if (!string.IsNullOrEmpty(allDataText))
            {
                AllDataXml.Outcome outcome = AllDataXml.TryRead(allDataText, out XElement root, out _, out string error);
                if (outcome == AllDataXml.Outcome.Ok && root != null)
                {
                    speakers = FromUnitRows(root);
                }
                else
                {
                    Debug.LogWarning(
                        "[ReplicCatalog] AllData could not be read (" + outcome + ": " + error +
                        ") — every enemy is silent. The unit → voice-set map lives in <vis replic='…'>.");
                }
            }

            return new ReplicCatalog(table, speakers);
        }

        /// <summary>
        /// Read the <c>&lt;unit&gt;</c> rows into a speaker map — a thin delegate, so the shipped path
        /// and the fixture take the <b>same</b> reader (<see cref="ReplicSpeakerMap.FromUnitRoot"/>).
        /// A second copy here would let the fixture pass while the game read the document differently.
        /// </summary>
        public static ReplicSpeakerMap FromUnitRows(XElement root) => ReplicSpeakerMap.FromUnitRoot(root);

        /// <summary>
        /// One guarded <c>Resources.Load</c>. A missing asset is a warning, never an exception — the
        /// same reason <c>PlayerPipInfoSource.AllDataRoot</c> swallows its own failure.
        /// </summary>
        private static string LoadResource(string path, string what)
        {
            try
            {
                TextAsset asset = Resources.Load<TextAsset>(path);
                if (asset != null) return asset.text;

                Debug.LogWarning(
                    "[ReplicCatalog] no TextAsset at Resources/" + path + " — " + what +
                    " cannot be read, so enemies will be silent.");
                return null;
            }
            catch (Exception)
            {
                // No engine here (offline, or a host without Resources). The caller's "silent" report
                // is the useful one.
                return null;
            }
        }
    }
}
