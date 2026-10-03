using System;
using System.Text;
using PFE.Data.Definitions;
using PFE.Entities.Player;
using PFE.Systems.Magic;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// Debug commands exposed to the Lua console as the global <c>spell</c> table, plus the console verb
    /// <c>spell</c> (sugar over the same methods).
    ///
    /// <para><b>Why this exists.</b> The spell caster landed with a working cast path and no way to
    /// acquire a spell. The port has no production <c>GameInventory</c>, so <c>invent.spells</c> has no
    /// producer and the only route to a spell was the F2 editor's weapon list — equipping one of the
    /// nine weapons whose <c>weapon@spell='1'</c>. That works, but it makes "does <c>C</c> cast?" and
    /// "can I equip this weapon?" the same question. This separates them: <c>spell add sp_mshit</c>
    /// grants the spell with no weapon involved.</para>
    ///
    /// <para><b>Status names the broken link, like <c>eff</c> and <c>rpg</c>.</b> A spell that will not
    /// cast has four different causes — no player, no caster component, no catalogue row, or a row the
    /// caster refuses — and they are indistinguishable from the far end. <c>spell</c> with no argument
    /// prints the one figure that separates them.</para>
    ///
    /// <para>A bare <c>spell</c> is <b>status</b>, never a toggle, matching <c>col</c>, <c>prof</c>,
    /// <c>rpg</c> and <c>eff</c>.</para>
    ///
    /// <para><b>MoonSharp visibility.</b> <c>Wire</c> is <c>internal</c>; MoonSharp's default reflection
    /// interop exposes public members only, so only the command methods below become callable from Lua.
    /// Do not make <c>_playerProvider</c> or <c>Wire</c> public.</para>
    /// </summary>
    [LocalOnly]
    public sealed class DevConsoleSpellCommands
    {
        /// <summary>
        /// Late-resolved so the commands survive a respawn — the same reason and the same shape as
        /// <see cref="DevConsoleEffectCommands"/>. A cached reference to a destroyed player is the
        /// classic "MissingReferenceException in a debug tool" failure.
        /// </summary>
        private Func<PlayerController> _playerProvider;

        internal void Wire(Func<PlayerController> playerProvider)
        {
            _playerProvider = playerProvider;
        }

        private static PlayerController FindPlayer()
            => UnityEngine.Object.FindFirstObjectByType<PlayerController>();

        private PlayerController ResolvePlayer()
        {
            var player = _playerProvider != null ? _playerProvider() : null;
            if (player == null) player = FindPlayer();
            return player;
        }

        /// <summary>
        /// The caster on the player. It is added by <c>PlayerController.Awake</c>, so it is present
        /// whenever the player is — but it is resolved rather than assumed, because "the component is
        /// missing" is one of the four causes this command exists to tell apart.
        /// </summary>
        private PlayerSpellCaster ResolveCaster(out PlayerController player)
        {
            player = ResolvePlayer();
            return player != null ? player.GetComponent<PlayerSpellCaster>() : null;
        }

        // ── Commands ──────────────────────────────────────────────────────────

        /// <summary>
        /// Engine health: is there a caster, does it own a book, how many spells does the player have,
        /// which one is selected, and how many rows can the catalogue offer. Ends with a verdict line
        /// that names the broken link.
        /// </summary>
        public string Status()
        {
            PlayerController player = ResolvePlayer();
            if (player == null)
                return "[spell] No PlayerController in the scene. Enter a gameplay room (SampleScene).";

            PlayerSpellCaster caster = player.GetComponent<PlayerSpellCaster>();
            int catalogCount = SpellCatalog.All().Length;

            var sb = new StringBuilder();
            sb.AppendLine("[spell] --- spell engine health ---");
            sb.AppendLine($"  caster component         : {(caster != null ? "YES" : "NO   <- PlayerController.Awake did not add it")}");
            sb.AppendLine($"  book built               : {(caster?.Book != null ? "YES" : "NO   <- Construct has not run")}");
            sb.AppendLine($"  spells known             : {caster?.SpellCount ?? 0}");
            sb.AppendLine($"  selected (Def key casts) : {(caster?.Selected != null ? caster.Selected.Id : "(none)")}");
            sb.AppendLine($"  catalogue rows           : {catalogCount} (Resources/Items with populated spellData)");

            sb.AppendLine();
            sb.AppendLine("  " + Verdict(caster, catalogCount));
            return sb.ToString();
        }

        /// <summary>The spells the player currently owns, in acquisition order — AS3's <c>invent.spells</c>.</summary>
        public string List()
        {
            PlayerSpellCaster caster = ResolveCaster(out PlayerController player);
            if (player == null) return "[spell] No PlayerController in the scene.";
            if (caster?.Book == null) return "[spell] The player has no spell book. Run `spell` for the diagnosis.";

            var sb = new StringBuilder();
            sb.AppendLine($"[spell] --- {caster.SpellCount} spell(s) known ---");

            if (caster.SpellCount == 0)
            {
                sb.AppendLine("  (none) — grant one with `spell add sp_mshit`, then press C in game.");
                return sb.ToString();
            }

            foreach (Spell spell in caster.Book.Spells)
            {
                if (spell == null) continue;

                string mark = ReferenceEquals(spell, caster.Selected) ? "▶" : " ";
                ItemDefinition row = SpellCatalog.Find(spell.Id);
                string facts = row != null
                    ? SpellFacts.Summarize(in row.spellData)
                    : "(no catalogue row)";

                sb.AppendLine($"  {mark} {spell.Id,-14} {facts}");
                if (spell.Produces)
                    sb.AppendLine("      repeats while the key is held (prod)");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Grant a spell — AS3 <c>Invent.addSpell</c>, the same call the inventory's <c>useItem</c>
        /// dispatch makes. Re-granting an owned spell is a no-op that reports so, rather than an error.
        /// </summary>
        public string Add(string spellId)
        {
            PlayerSpellCaster caster = ResolveCaster(out PlayerController player);
            if (player == null) return "[spell] No PlayerController in the scene.";
            if (string.IsNullOrEmpty(spellId))
                return "Usage: spell add <spellId>    e.g. spell add sp_mshit   (see `spell defs`)";
            if (caster?.Book == null) return "[spell] The player has no spell book. Run `spell` for the diagnosis.";

            bool alreadyKnown = caster.Book.TryGet(spellId, out Spell _);

            Spell spell = caster.LearnSpell(spellId);
            if (spell == null)
            {
                // The only way LearnSpell returns null is a factory refusal, and the factory refuses on
                // exactly two conditions — no item row, or a row whose spellData was never populated.
                // Name which, because they have different fixes (run the importer, or the row is not a
                // spell at all).
                ItemDefinition row = SpellCatalog.Find(spellId);
                return row == null
                    ? $"[spell] No spell row '{spellId}'. There are {SpellCatalog.All().Length} under " +
                      $"Resources/Items — run `spell defs` to list them."
                    : $"[spell] '{spellId}' has an item row but its spellData is EMPTY, so the caster " +
                      $"refuses it. Re-run `PFE/Data/Import Effects from AllData.as` (or the item " +
                      $"importer) to populate it.";
            }

            return alreadyKnown
                ? $"[spell] '{spell.Id}' was already known ({caster.SpellCount} spell(s))."
                : $"[spell] Granted '{spell.Id}'. The player now knows {caster.SpellCount} spell(s). " +
                  $"Select it with `spell select {spell.Id}`, then press C in game.";
        }

        /// <summary>
        /// Select the spell the Def key will cast — AS3 <c>UnitPlayer.changeSpell</c>, which is a
        /// <b>toggle</b>: selecting the already-selected spell deselects it. That is the oracle's
        /// behaviour, not a bug, so this reports which way it went.
        /// </summary>
        public string Select(string spellId)
        {
            PlayerSpellCaster caster = ResolveCaster(out PlayerController player);
            if (player == null) return "[spell] No PlayerController in the scene.";
            if (string.IsNullOrEmpty(spellId))
                return "Usage: spell select <spellId>    (a toggle — the same id twice deselects)";
            if (caster?.Book == null) return "[spell] The player has no spell book. Run `spell` for the diagnosis.";

            if (!caster.Book.TryGet(spellId, out Spell _))
                return $"[spell] The player does not know '{spellId}'. Grant it first: `spell add {spellId}`.";

            bool wasSelected = caster.Selected != null &&
                               string.Equals(caster.Selected.Id, spellId, StringComparison.OrdinalIgnoreCase);

            Spell now = caster.SelectSpell(spellId);

            return now == null
                ? $"[spell] '{spellId}' was already selected — it is now DESELECTED (changeSpell is a " +
                  $"toggle; the Def key will do nothing)."
                : $"[spell] '{spellId}' is now the selected spell. Press C in game to cast it.";
        }

        /// <summary>
        /// The catalogue the caster can accept, optionally filtered by a substring of the id or the
        /// name. What <c>add</c> draws from, so an id that appears here but is refused by <c>add</c> is
        /// an import problem, not a naming problem.
        /// </summary>
        public string Defs(string filter = null) => "[spell] " + SpellCatalog.Dump(filter);

        /// <summary>Usage for the <c>spell</c> verb.</summary>
        public string Help()
        {
            return
                "spell                   - spell engine health: caster, book, selection, catalogue size\n" +
                "spell list              - the spells the player knows, in acquisition order\n" +
                "spell add <id>          - grant a spell (no weapon needed); see `spell defs`\n" +
                "spell select <id>       - choose the spell the Def key (C) casts — a TOGGLE\n" +
                "spell defs [filter]     - the catalogue the caster can accept\n" +
                "Lua: spell:Status() | spell:Add(\"sp_mshit\") | spell:Select(\"sp_mshit\")";
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string Verdict(PlayerSpellCaster caster, int catalogCount)
        {
            if (caster == null)
                return "VERDICT: DEAD -- the player has no PlayerSpellCaster. PlayerController.Awake adds " +
                       "one; if it is missing, the component was removed or Awake threw before that line.";

            if (caster.Book == null)
                return "VERDICT: DEAD -- the caster exists but its book was never built, so Construct has " +
                       "not run. That happens when the container never injected the player.";

            if (catalogCount == 0)
                return "VERDICT: EMPTY -- the book is wired but Resources/Items has no row with " +
                       "populated spellData, so nothing can be granted. Run the item importer " +
                       "(`PFE/Data/Simple Import All Data`).";

            if (caster.SpellCount == 0)
                return "VERDICT: LIVE but empty -- the caster is wired and the catalogue has rows; the " +
                       "player knows no spells. Try `spell add sp_mshit`.";

            if (caster.Selected == null)
                return $"VERDICT: LIVE -- {caster.SpellCount} spell(s) known but NONE selected, so the " +
                       "Def key (C) does nothing. `spell select <id>` fixes that.";

            return $"VERDICT: LIVE -- {caster.SpellCount} spell(s), '{caster.Selected.Id}' selected. " +
                   "Press C in game.";
        }
    }
}
