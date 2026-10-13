using System;
using System.Collections.Generic;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// Serialises the story-trigger table into the save DTO's parallel arrays and back.
    ///
    /// <para><b>Why this is a separate, Unity-free class.</b> The logic lives in <c>SaveManager</c>
    /// normally, but <c>SaveManager</c> reaches <c>JsonUtility</c> (an ECall) and so is unreachable on
    /// the offline wall — every fixture that drives it dies in setup. Extracting the transform here means
    /// the part that can actually be <i>wrong</i> — the string arm, and the legacy int-only format —
    /// is exercised by a fixture that runs.</para>
    ///
    /// <para>Unity's <c>JsonUtility</c> cannot serialise a <c>Dictionary</c> or a variant payload, which
    /// is why the DTO is four parallel arrays rather than a map. The arrays must stay index-aligned;
    /// <see cref="Rebuild"/> tolerates a short array by treating the missing tail as unset rather than
    /// throwing, because a save file is user data and a truncated one must not crash the load.</para>
    /// </summary>
    public static class TriggerTableCodec
    {
        /// <summary>Kind codes as they appear in the save file. They are written as plain ints rather
        /// than the enum so a save is readable and stable across a refactor of the enum.</summary>
        public const int KindUnset = 0;
        public const int KindInt = 1;
        public const int KindString = 2;

        /// <summary>
        /// Flatten <paramref name="table"/> into the four arrays the save DTO carries. A null table
        /// yields empty arrays, never nulls, so the writer always produces a complete block.
        /// </summary>
        public static void Flatten(
            IReadOnlyDictionary<string, TriggerValue> table,
            out string[] names,
            out int[] values,
            out string[] strings,
            out int[] kinds)
        {
            if (table == null)
            {
                names = Array.Empty<string>();
                values = Array.Empty<int>();
                strings = Array.Empty<string>();
                kinds = Array.Empty<int>();
                return;
            }

            var nameList = new List<string>(table.Count);
            var valueList = new List<int>(table.Count);
            var stringList = new List<string>(table.Count);
            var kindList = new List<int>(table.Count);

            foreach (KeyValuePair<string, TriggerValue> pair in table)
            {
                if (string.IsNullOrEmpty(pair.Key)) continue;

                TriggerValue v = pair.Value;
                nameList.Add(pair.Key);
                kindList.Add((int)v.Kind);
                valueList.Add(v.Kind == TriggerValue.TriggerKind.Int ? v.RawInt : 0);
                stringList.Add(v.Kind == TriggerValue.TriggerKind.String ? v.RawString : string.Empty);
            }

            names = nameList.ToArray();
            values = valueList.ToArray();
            strings = stringList.ToArray();
            kinds = kindList.ToArray();
        }

        /// <summary>
        /// Rebuild the table from the save DTO's arrays.
        ///
        /// <para><b><paramref name="kinds"/> being null means a legacy save</b> — one written when the
        /// table was <c>int</c>-only. Every entry then reads as <see cref="TriggerValue.FromInt"/>, which
        /// is the only faithful reconstruction: that build could not have written a string. This is the
        /// <c>hasCheckpoint</c> pattern (<c>WorldSaveData.cs</c>), where the absence of a block must read
        /// as a coherent old state rather than as a new state full of zeroes.</para>
        /// </summary>
        public static List<KeyValuePair<string, TriggerValue>> Rebuild(
            string[] names,
            int[] values,
            string[] strings,
            int[] kinds)
        {
            var result = new List<KeyValuePair<string, TriggerValue>>();
            if (names == null) return result;

            // The legacy decision is made ONCE, for the whole block — not per entry. `kinds == null`
            // means the save predates the widened value type, so every entry is an int. A *present but
            // short* kinds array is a different case: a malformed current-format save, whose tail has an
            // unknown kind, and the honest answer there is Unset rather than a fabricated 0.
            bool legacyIntOnlyFormat = kinds == null;

            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                if (string.IsNullOrEmpty(name)) continue;

                TriggerValue value;

                if (legacyIntOnlyFormat)
                {
                    value = TriggerValue.FromInt(values != null && i < values.Length ? values[i] : 0);
                }
                else if (i < kinds.Length)
                {
                    switch (kinds[i])
                    {
                        case KindString:
                            value = TriggerValue.FromString(
                                strings != null && i < strings.Length ? strings[i] : null);
                            break;

                        case KindInt:
                            value = TriggerValue.FromInt(
                                values != null && i < values.Length ? values[i] : 0);
                            break;

                        default:
                            value = TriggerValue.Unset;
                            break;
                    }
                }
                else
                {
                    // Malformed current-format tail: the kind is unknown, so do not invent a value.
                    value = TriggerValue.Unset;
                }

                result.Add(new KeyValuePair<string, TriggerValue>(name, value));
            }

            return result;
        }
    }
}
