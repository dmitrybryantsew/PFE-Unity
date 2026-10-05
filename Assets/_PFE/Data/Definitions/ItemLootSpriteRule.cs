using System;
using System.Collections.Generic;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Which SWF symbol an item's <b>loot sprite</b> comes from.
    ///
    /// <para>AS3 resolves three different clips depending on the item's <c>tip</c>
    /// (<c>fe/loc/Loot.as:85-220</c>), and the three are not interchangeable: two of them are different
    /// symbols entirely, and the third is a per-item symbol whose name is built from the item id.</para>
    /// </summary>
    public enum ItemLootSpriteSource
    {
        /// <summary>The shared <c>visualItem</c> clip — the general branch. Most items.</summary>
        VisualItem,

        /// <summary>The shared <c>visualAmmo</c> clip — ammunition.</summary>
        VisualAmmo,

        /// <summary>A per-item <c>vis&lt;itemId&gt;</c> clip — explosives and held weapons.</summary>
        PerItemVis,
    }

    /// <summary>
    /// One resolved loot sprite: which symbol, and which frame of it.
    /// </summary>
    public readonly struct ItemLootSprite
    {
        /// <summary>Symbol name from the export's symbol table, e.g. <c>visualItem</c>. Never null when resolved.</summary>
        public readonly string SymbolName;

        /// <summary>1-based frame of that symbol, matching the JPEXS export's <c>{n}.png</c>.</summary>
        public readonly int FrameNumber;

        /// <summary>Which of the oracle's three branches produced this.</summary>
        public readonly ItemLootSpriteSource Source;

        /// <summary>Why this frame, in one line — for the importer's report and for test failure messages.</summary>
        public readonly string Detail;

        public ItemLootSprite(string symbolName, int frameNumber, ItemLootSpriteSource source, string detail)
        {
            SymbolName = symbolName;
            FrameNumber = frameNumber;
            Source = source;
            Detail = detail;
        }

        /// <summary>True when a usable symbol and frame came out.</summary>
        public bool IsResolved => !string.IsNullOrEmpty(SymbolName) && FrameNumber >= 1;

        public override string ToString() =>
            IsResolved ? $"{SymbolName} frame {FrameNumber} ({Detail})" : $"unresolved ({Detail})";
    }

    /// <summary>
    /// The rule that decides which sprite an item shows when it is lying on the ground — the port's
    /// statement of AS3 <c>fe/loc/Loot.as</c>'s constructor.
    ///
    /// <para><b>Why this lives in the runtime assembly.</b> The same reason
    /// <see cref="WeaponVisSymbolRule"/> and <see cref="AllDataXml"/> do: the offline test wall cannot
    /// reach <c>PFE.Editor</c>, so a decision that lives in an editor importer is a decision no test
    /// can pin. This one is worth pinning because it is entirely data-driven and its failure mode is
    /// silent — an item that resolves to the wrong frame draws a plausible, wrong icon, and nothing
    /// goes red.</para>
    ///
    /// <para><b>The oracle, in full.</b> <c>Loot.as</c> picks the clip in four arms:</para>
    /// <list type="number">
    ///   <item><description><b>Weapon</b> (<c>:85-124</c>) — <c>Res.getClass("vis"+id, null, visp10mm)</c>,
    ///     then <c>infIco.stop()</c>, i.e. frame 1 of a per-item symbol.</description></item>
    ///   <item><description><b>Explosive</b> (<c>:125-138</c>) — <c>Res.getClass("vis"+id, null, visualAmmo)</c>,
    ///     <c>infIco.stop()</c>: frame 1 of the per-item symbol, or of <c>visualAmmo</c> if the item has
    ///     none.</description></item>
    ///   <item><description><b>Ammunition</b> (<c>:139-162</c>) — <c>visualAmmo</c>, then
    ///     <c>gotoAndStop(xml.@base)</c>, else <c>gotoAndStop(id)</c>, and on any failure
    ///     <c>gotoAndStop(1)</c>.</description></item>
    ///   <item><description><b>Everything else</b> (<c>:163-220</c>) — <c>visualItem</c>, then
    ///     <c>gotoAndStop(id)</c>; on failure a <c>tip</c>-based fallback label, and failing that frame
    ///     1. <c>tip == "scheme"</c> then <i>unconditionally</i> re-points to the <c>"scheme"</c>
    ///     label.</description></item>
    /// </list>
    ///
    /// <para><b>Why <c>tip</c> and not <see cref="ItemType"/>.</b> The port's <c>ItemType</c> cannot
    /// express this decision. <c>compa</c>, <c>compw</c>, <c>compe</c>, <c>compp</c> and <c>compm</c>
    /// all collapse to <c>ItemType.Component</c>, and <c>paint</c>, <c>food</c> and <c>scheme</c> all
    /// collapse to <c>ItemType.Misc</c> — so a rule written against the enum could not tell
    /// <c>paint</c> from <c>food</c> and would silently give one of them the other's icon. The
    /// fallback table is keyed on the raw <c>tip</c> string for exactly that reason, which means the
    /// caller must read <c>tip</c> from the source XML rather than from the asset.</para>
    /// </summary>
    public static class ItemLootSpriteRule
    {
        // ── Symbols ──────────────────────────────────────────────────────────
        // Both verified against pfe/symbolClass/symbols.csv: 4356;"visualItem", 3737;"visualAmmo".

        /// <summary>The shared clip for ordinary items — SWF symbol 4356.</summary>
        public const string VisualItemSymbol = "visualItem";

        /// <summary>The shared clip for ammunition — SWF symbol 3737.</summary>
        public const string VisualAmmoSymbol = "visualAmmo";

        /// <summary>Prefix AS3 builds a per-item clip name from: <c>vis</c> + item id.</summary>
        public const string VisPrefix = "vis";

        /// <summary>The frame every failing arm lands on — <c>gotoAndStop(1)</c>.</summary>
        public const int FallbackFrame = 1;

        // ── tip constants, verbatim from AS3 fe/serv/Item.as ──────────────────
        // Note these are NOT the port's ItemType names and must not be mapped through it.

        public const string TipWeapon = "weapon";
        public const string TipExplosive = "e";
        public const string TipAmmo = "a";
        public const string TipScheme = "scheme";
        public const string TipCompA = "compa";
        public const string TipCompW = "compw";
        public const string TipCompE = "compe";
        public const string TipCompP = "compp";
        public const string TipKey = "key";
        public const string TipPaint = "paint";
        public const string TipFood = "food";

        /// <summary>The <c>visualItem</c> frame label AS3 falls back to for <c>tip == "scheme"</c>.</summary>
        public const string SchemeLabel = "scheme";

        /// <summary>
        /// The fallback frame label for an item whose id is not itself a label — the
        /// <c>catch</c> block of <c>Loot.as:173-204</c>. Returns <c>null</c> when the oracle would go
        /// straight to frame 1, which is the case for every other tip.
        ///
        /// <para><c>compm</c> is deliberately absent: AS3 has a <c>L_COMPM</c> constant but the catch
        /// block never tests it, so a <c>compm</c> item without its own label lands on frame 1. Reading
        /// the constant list instead of the <c>if</c> chain is the natural mistake here, and it would
        /// hand every material component a wrong-but-plausible icon.</para>
        /// </summary>
        public static string FallbackFrameLabel(string tip)
        {
            switch (tip)
            {
                case TipCompA: return TipCompA;
                case TipCompW: return TipCompW;
                case TipCompE: return TipCompE;
                case TipCompP: return TipCompP;
                case TipKey: return TipKey;
                case TipPaint: return TipPaint;
                case TipFood: return TipFood;
                default: return null;
            }
        }

        /// <summary>Builds the per-item clip name AS3 resolves: <c>vis</c> + item id.</summary>
        public static string PerItemSymbolName(string itemId) =>
            string.IsNullOrEmpty(itemId) ? null : VisPrefix + itemId;

        /// <summary>
        /// Resolve the loot sprite for one item.
        /// </summary>
        /// <param name="itemId">AS3 <c>item.id</c> — also the frame label on the shared clips.</param>
        /// <param name="tip">AS3 <c>item.tip</c>, read from the source XML. May be null/empty.</param>
        /// <param name="baseLabel">AS3 <c>item.xml.@base</c>, ammunition only. May be null.</param>
        /// <param name="hasPerItemVisSymbol">
        /// Whether the export actually contains a <c>vis&lt;itemId&gt;</c> symbol. The oracle asks
        /// <c>Res.getClass</c> for it and takes its default when absent, so the caller has to answer
        /// this from the symbol table.
        /// </param>
        /// <param name="visualItemLabels">Frame label → 1-based frame, for <c>visualItem</c>.</param>
        /// <param name="visualAmmoLabels">Frame label → 1-based frame, for <c>visualAmmo</c>.</param>
        public static ItemLootSprite Resolve(
            string itemId,
            string tip,
            string baseLabel,
            bool hasPerItemVisSymbol,
            IReadOnlyDictionary<string, int> visualItemLabels,
            IReadOnlyDictionary<string, int> visualAmmoLabels)
        {
            if (string.IsNullOrEmpty(itemId))
                return new ItemLootSprite(null, 0, ItemLootSpriteSource.VisualItem, "no item id");

            // ── Arm 1: held weapons (Loot.as:85-124) ─────────────────────────
            if (tip == TipWeapon)
            {
                string symbol = PerItemSymbolName(itemId);
                if (!hasPerItemVisSymbol)
                    return new ItemLootSprite(null, 0, ItemLootSpriteSource.PerItemVis,
                        $"tip 'weapon' wants '{symbol}', which the export does not contain");

                return new ItemLootSprite(symbol, FallbackFrame, ItemLootSpriteSource.PerItemVis,
                    $"tip 'weapon' → Res.getClass(\"{symbol}\", …, visp10mm); infIco.stop()");
            }

            // ── Arm 2: explosives (Loot.as:125-138) ──────────────────────────
            if (tip == TipExplosive)
            {
                if (hasPerItemVisSymbol)
                {
                    string symbol = PerItemSymbolName(itemId);
                    return new ItemLootSprite(symbol, FallbackFrame, ItemLootSpriteSource.PerItemVis,
                        $"tip 'e' → Res.getClass(\"{symbol}\", …, visualAmmo); infIco.stop()");
                }

                return new ItemLootSprite(VisualAmmoSymbol, FallbackFrame, ItemLootSpriteSource.VisualAmmo,
                    "tip 'e' with no vis<id> symbol → visualAmmo default");
            }

            // ── Arm 3: ammunition (Loot.as:139-162) ──────────────────────────
            //
            // `@base` and the id are NOT two attempts at the same thing: the oracle picks one with
            // `if (xml.@base.length()) … else …`, and a failure of whichever it picked goes to the
            // catch, i.e. frame 1. So when `@base` is present but unknown, the id label is never
            // consulted — an item whose @base is stale lands on frame 1 even though its own id is a
            // perfectly good label. Retrying the id here would look more "robust" and would be a
            // divergence: it would draw a different sprite from the one the oracle draws.
            if (tip == TipAmmo)
            {
                if (!string.IsNullOrEmpty(baseLabel))
                {
                    if (TryFrame(visualAmmoLabels, baseLabel, out int baseFrame))
                    {
                        return new ItemLootSprite(VisualAmmoSymbol, baseFrame, ItemLootSpriteSource.VisualAmmo,
                            $"tip 'a' → xml.@base='{baseLabel}'");
                    }

                    return new ItemLootSprite(VisualAmmoSymbol, FallbackFrame, ItemLootSpriteSource.VisualAmmo,
                        $"tip 'a' → xml.@base='{baseLabel}' is not a visualAmmo label → catch → frame 1 " +
                        "(the id label is deliberately not retried)");
                }

                if (TryFrame(visualAmmoLabels, itemId, out int idFrame))
                {
                    return new ItemLootSprite(VisualAmmoSymbol, idFrame, ItemLootSpriteSource.VisualAmmo,
                        $"tip 'a' → no xml.@base → id label '{itemId}'");
                }

                return new ItemLootSprite(VisualAmmoSymbol, FallbackFrame, ItemLootSpriteSource.VisualAmmo,
                    $"tip 'a' → no xml.@base and no id label → catch → frame 1");
            }

            // ── Arm 4: everything else (Loot.as:163-220) ─────────────────────
            int frame;
            string detail;

            if (TryFrame(visualItemLabels, itemId, out int directFrame))
            {
                frame = directFrame;
                detail = $"id label '{itemId}'";
            }
            else
            {
                string fallback = FallbackFrameLabel(tip);
                if (fallback != null && TryFrame(visualItemLabels, fallback, out int fallbackFrame))
                {
                    frame = fallbackFrame;
                    detail = $"no id label; tip '{tip}' → '{fallback}'";
                }
                else
                {
                    frame = FallbackFrame;
                    detail = fallback == null
                        ? $"no id label and no fallback for tip '{tip}' → frame 1"
                        : $"no id label, and '{fallback}' is not a visualItem label → frame 1";
                }
            }

            // The unconditional re-point (Loot.as:206-210). It runs AFTER the try/catch, so it wins
            // over a matching id label — this is not a fallback, it is an override.
            if (tip == TipScheme)
            {
                if (TryFrame(visualItemLabels, SchemeLabel, out int schemeFrame))
                {
                    frame = schemeFrame;
                    detail = $"tip 'scheme' → '{SchemeLabel}' override (after '{detail}')";
                }
                else
                {
                    detail = $"tip 'scheme' wanted '{SchemeLabel}', which the clip lacks; kept {detail}";
                }
            }

            return new ItemLootSprite(VisualItemSymbol, frame, ItemLootSpriteSource.VisualItem, detail);
        }

        /// <summary>
        /// Dictionary lookup that treats a missing map as a miss rather than throwing — the caller may
        /// legitimately have no label data at all (a run with no SWF path), and that must degrade to
        /// "frame 1", not to a null reference.
        /// </summary>
        private static bool TryFrame(IReadOnlyDictionary<string, int> labels, string label, out int frame)
        {
            frame = 0;
            if (labels == null || string.IsNullOrEmpty(label)) return false;
            return labels.TryGetValue(label, out frame) && frame >= 1;
        }
    }
}
