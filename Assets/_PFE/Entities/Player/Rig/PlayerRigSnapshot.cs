using System;
using System.Collections.Generic;
using System.Globalization;

namespace PFE.Entities.Player.Rig
{
    /// <summary>How a candidate rig's tunable differs from the baseline player's.</summary>
    public enum RigDivergenceKind
    {
        /// <summary>Both sides have the key; the values differ.</summary>
        ValueDiffers,

        /// <summary>The baseline (prefab player) has the key; the candidate (code rig) does not.</summary>
        MissingInCandidate,

        /// <summary>The candidate has the key; the baseline does not.</summary>
        MissingInBaseline,
    }

    /// <summary>One disagreement between two <see cref="PlayerRigSnapshot"/>s.</summary>
    public readonly struct RigDivergence
    {
        public readonly string Key;
        public readonly string Baseline;
        public readonly string Candidate;
        public readonly RigDivergenceKind Kind;

        public RigDivergence(string key, string baseline, string candidate, RigDivergenceKind kind)
        {
            Key = key;
            Baseline = baseline;
            Candidate = candidate;
            Kind = kind;
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case RigDivergenceKind.ValueDiffers:
                    return Key + ": " + Baseline + " -> " + Candidate;
                case RigDivergenceKind.MissingInCandidate:
                    return Key + ": " + Baseline + " (absent from code rig)";
                default:
                    return Key + ": (absent from prefab) -> " + Candidate;
            }
        }
    }

    /// <summary>
    /// A flat, Unity-free record of the player tunables that decide how a player behaves.
    ///
    /// <para><b>Why a plain class with string values, and why it lives here.</b> The same reason
    /// <c>OverlayClip</c> holds the overlay rule and the assembler holds only the assignment: the
    /// decision ("these two rigs disagree, here is where") is worth testing, and a decision that
    /// mentions a <c>UnityEngine</c> type cannot be tested off the editor. Values are stored as
    /// text so the comparison is a total function over strings, with no float tolerance to argue
    /// about and no locale to leak in.</para>
    ///
    /// <para>Ordering is <see cref="StringComparer.Ordinal"/> over the key, so <see cref="Diff"/>
    /// is deterministic and a diff report can be asserted line by line.</para>
    /// </summary>
    public sealed class PlayerRigSnapshot
    {
        // Group prefixes. Consts so the reader, the writer and the tests all name the same thing.
        public const string TilePhysics = "tilePhysics";
        public const string Locomotion = "locomotion";
        public const string Abilities = "abilities";
        public const string Mounts = "mounts";
        public const string Character = "character";

        readonly SortedDictionary<string, string> _values =
            new SortedDictionary<string, string>(StringComparer.Ordinal);

        public int Count => _values.Count;

        public IReadOnlyDictionary<string, string> Values => _values;

        public void Set(string key, string value) => _values[key] = value ?? "<null>";

        public void Set(string key, bool value) => Set(key, value ? "true" : "false");

        public void Set(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

        public void Set(string key, float value) => Set(key, FormatFloat(value));

        public bool TryGet(string key, out string value) => _values.TryGetValue(key, out value);

        /// <summary>
        /// Round-trippable, culture-invariant float text.
        ///
        /// <para><c>"R"</c> is the shortest string that reads back to the identical value, which is
        /// what makes an exact string comparison a valid equality test for a float. A
        /// culture-sensitive format would make the diff locale-dependent — the same rig would
        /// compare equal on one machine and different on another.</para>
        /// </summary>
        public static string FormatFloat(float value) =>
            value.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>
        /// Every key that differs, in either direction. A key present on one side only is a
        /// divergence too — an extra tunable on the rig is as much a finding as a missing one.
        /// </summary>
        public static List<RigDivergence> Diff(PlayerRigSnapshot baseline, PlayerRigSnapshot candidate)
        {
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));

            var result = new List<RigDivergence>();

            foreach (KeyValuePair<string, string> kv in baseline._values)
            {
                string other;
                if (!candidate._values.TryGetValue(kv.Key, out other))
                {
                    result.Add(new RigDivergence(kv.Key, kv.Value, "<absent>",
                        RigDivergenceKind.MissingInCandidate));
                }
                else if (!string.Equals(kv.Value, other, StringComparison.Ordinal))
                {
                    result.Add(new RigDivergence(kv.Key, kv.Value, other,
                        RigDivergenceKind.ValueDiffers));
                }
            }

            foreach (KeyValuePair<string, string> kv in candidate._values)
            {
                if (!baseline._values.ContainsKey(kv.Key))
                {
                    result.Add(new RigDivergence(kv.Key, "<absent>", kv.Value,
                        RigDivergenceKind.MissingInBaseline));
                }
            }

            return result;
        }
    }
}
