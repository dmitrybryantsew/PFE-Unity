using System.Collections.Generic;
using System.Text;
using PFE.Data.Definitions;

namespace PFE.Systems.Magic
{
    /// <summary>
    /// Turns a <see cref="SpellData"/> row into human-readable text, for the F2 debug editor's Spells
    /// tab and the <c>spell</c> console verb.
    ///
    /// <para><b>Why the description is derived rather than authored.</b> The oracle has no per-spell
    /// description to port. Every spell row in <c>AllData.as:4008-4016</c> carries
    /// <c>mess='spell'</c>, and that is a <i>category tag</i>, not prose — <c>serv/Item.as:303-305</c>
    /// stores it in <c>Item.mess</c> and nothing ever resolves it to a string. The port's spell item
    /// assets carry the importer's placeholder (<c>displayName: Item Name</c>,
    /// <c>description: Item description.</c>), verified on <c>Items/sp_mshit.asset</c>. So there is no
    /// text to copy, and inventing lore would be worse than deriving facts: every line below traces to
    /// a field whose own doc comment cites the oracle line it came from.</para>
    ///
    /// <para><b>Unity-free on purpose.</b> <see cref="SpellData"/> is a plain struct of
    /// <c>float</c>/<c>bool</c>/<c>string</c>, so this class carries no <c>ECall</c> and runs offline —
    /// which is what lets <c>SpellFactsTests</c> prove the wording without an editor. The
    /// <see cref="UnityEngine.ScriptableObject"/> that owns the row stays on the caller's side.</para>
    /// </summary>
    public static class SpellFacts
    {
        /// <summary>
        /// The cast-sound id when the row has one, else <c>null</c>. AS3 <c>@snd</c>
        /// (<c>Spell.as:129-131</c>). All nine shipped spells set it, which is also why
        /// <see cref="SpellData.IsPopulated"/> uses it as its discriminator.
        /// </summary>
        public static string SoundId(in SpellData data)
            => string.IsNullOrEmpty(data.snd) ? null : data.snd;

        /// <summary>
        /// The mechanical facts, one short line — what the spell does, in the oracle's own terms.
        ///
        /// <para>Field order follows the cast, not the struct: what it hits, then what it costs, then
        /// what it needs. A zero means "absent" for every one of these, because AS3's constructor guards
        /// each attribute on presence (<c>Spell.as:83-131</c>), so an unset attribute is not a literal
        /// zero — see <see cref="SpellData"/>'s remarks.</para>
        /// </summary>
        public static string Summarize(in SpellData d)
        {
            var parts = new List<string>(6);

            // What it does.
            if (d.dam > 0f) parts.Add($"damage {Trim(d.dam)}");
            if (d.rad > 0f) parts.Add($"radius {Trim(d.rad)}");
            if (d.hp > 0f) parts.Add($"creates {Trim(d.hp)} hp");
            if (d.dist > 0f) parts.Add($"range {Trim(d.dist)}");

            // What it costs — the budget first, then the organ, matching the affordability test order
            // (Spell.as:229 tests dmagic before dmana at :237).
            if (d.magic > 0f) parts.Add($"{Trim(d.magic)} magic");
            if (d.mana > 0f) parts.Add($"{Trim(d.mana)} mana");

            // What it needs.
            if (d.line) parts.Add("line of sight");
            if (d.culd > 0f) parts.Add($"cooldown {Trim(d.culd)}s");

            return parts.Count == 0 ? "(no attributes)" : string.Join(", ", parts);
        }

        /// <summary>
        /// The behavioural flags, as words. These are the ones that change how the spell is <i>used</i>
        /// rather than what it costs, so they are kept separate from <see cref="Summarize"/>.
        /// </summary>
        public static string Flags(in SpellData d)
        {
            var parts = new List<string>(4);
            if (d.atk) parts.Add("offensive");          // Spell.as:124-126; needs gg.atkPoss too
            if (d.prod) parts.Add("repeats while held"); // Spell.as:116-118; only sp_cryst
            if (d.tele) parts.Add("telekinetic");        // Spell.as:120-122; sp_blast, sp_kdash
            return parts.Count == 0 ? "-" : string.Join(", ", parts);
        }

        /// <summary>
        /// The full multi-line description. <paramref name="displayName"/> is the item row's own name
        /// when the importer filled it in, and is deliberately allowed to be the placeholder — the
        /// caller decides whether to show it, and <see cref="IsPlaceholderName"/> answers that.
        /// </summary>
        public static string Describe(string spellId, string displayName, in SpellData d)
        {
            var sb = new StringBuilder();
            sb.Append(IsPlaceholderName(displayName) ? spellId : displayName);
            sb.Append(" — ").AppendLine(Summarize(in d));
            sb.Append("  flags: ").AppendLine(Flags(in d));
            string snd = SoundId(in d);
            sb.Append("  cast sound: ").Append(snd ?? "(none)");
            return sb.ToString();
        }

        /// <summary>
        /// True when <paramref name="name"/> is absent or is the importer's placeholder rather than a
        /// real name.
        ///
        /// <para><b>Why this is needed at all.</b> The importer writes
        /// <c>displayName = "Item Name"</c> (the field's C# initializer in
        /// <c>ItemDefinition.cs:279</c>) for every row it does not have a name for, and it has none for
        /// spells. Showing "Item Name" in a spell list reads as a bug, so the id is shown instead.</para>
        /// </summary>
        public static bool IsPlaceholderName(string name)
            => string.IsNullOrWhiteSpace(name) || name == "Item Name";

        /// <summary>
        /// A number without a trailing <c>.0</c> — the attributes are floats but every shipped value is
        /// an integer, and <c>500</c> reads better than <c>500.0</c> in a list.
        /// </summary>
        private static string Trim(float v)
            => v == (int)v ? ((int)v).ToString() : v.ToString("0.##");
    }
}
