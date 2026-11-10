using System;
using System.Collections.Generic;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Effect id → index into the imported icon sequence
    /// (<see cref="PFE.Data.Definitions.PipBuckCompositionDefinition.statusEffectIcons"/>, symbol
    /// <c>_effectIco_174</c> = 4453, 56 frames).
    ///
    /// <para><b>Where the mapping comes from.</b> AS3 never names a frame number: the HUD calls
    /// <c>veff[i].vis.gotoAndStop(this.gg.effects[n].id)</c> (<c>GUI.as:1220</c>), so the binding is the
    /// <b>frame label</b> on sprite 4453. Those labels are not in any exported text file — they are
    /// <c>FrameLabel</c> tags inside <c>assets.swf</c>, read with
    /// <c>.workbuddy-ai/tools/swf_sprite_layout.py … 4453 --labels</c>. The table below is that dump in
    /// frame order, and it must not be "tidied": the sequence is the file's, including the
    /// <c>post_*</c> comedown frames that sit next to their parent effect.</para>
    ///
    /// <para><b>Frame 1 is blank and is not in the table.</b> It is the frame the oracle's
    /// <c>catch</c> falls back to (<c>veff[i].vis.gotoAndStop(1)</c>, <c>GUI.as:1225</c>) — the imported
    /// <c>f001.png</c> is fully transparent, which is the confirmation that the importer's numbering is
    /// the SWF's. So a sprite index is <c>frame - 1</c>, and every id here is therefore ≥ 1.</para>
    ///
    /// <para><b>Three of these are not effects at all.</b> <c>cut</c>, <c>poison</c> and <c>shit</c> are
    /// the HUD's three hard-coded priority slots (<c>GUI.as:1171-1197</c>), keyed off the player's
    /// <c>cut</c>/<c>poison</c>/<c>shithp</c> fields rather than off <c>gg.effects</c>; <c>food</c> is the
    /// shared icon every <c>tip == 3</c> effect is drawn with (<c>:1214-1217</c>). They live in the same
    /// sprite because the oracle draws them through the same <c>gotoAndStop</c>.</para>
    /// </summary>
    public static class StatusEffectIcons
    {
        /// <summary>Frame count of symbol 4453 — <c>_effectIco_174</c>, 56 frames including the blank first one.</summary>
        public const int IconCount = 56;

        /// <summary>The id of the blank frame (frame 1) — what an unknown effect draws.</summary>
        public const int Blank = 0;

        /// <summary>
        /// Frame labels of sprite 4453 in frame order, <b>starting at frame 2</b>. Position <c>p</c> is
        /// frame <c>p + 2</c>, i.e. sprite index <c>p + 1</c>.
        /// </summary>
        private static readonly string[] IdsByFrame =
        {
            "cut",             // frame 2
            "poison",          // 3
            "chemburn",        // 4
            "shit",            // 5
            "burning",         // 6
            "namok",           // 7
            "stealth",         // 8
            "stealth_armor",   // 9
            "antidote",        // 10
            "radx",            // 11
            "mint",            // 12
            "pmint",           // 13
            "post_mint",       // 14
            "dash",            // 15
            "ultradash",       // 16
            "post_dash",       // 17
            "rage",            // 18
            "post_rage",       // 19
            "medx",            // 20
            "post_medx",       // 21
            "stampede",        // 22
            "post_stampede",   // 23
            "buck",            // 24
            "post_buck",       // 25
            "hydra",           // 26
            "post_hydra",      // 27
            "potion_fly",      // 28
            "potion_swim",     // 29
            "potion_infra",    // 30
            "potion_mage",     // 31
            "potion_shadow",   // 32
            "potion_pink",     // 33
            "potion_chance",   // 34
            "post_chance",     // 35
            "reanim",          // 36
            "inhibitor",       // 37
            "disorient",       // 38
            "horror",          // 39
            "vote",            // 40
            "freezing",        // 41
            "blindness",       // 42
            "contusion",       // 43
            "curse",           // 44
            "drunk",           // 45
            "food",            // 46
            "coffee",          // 47
            "stupor",          // 48
            "weak",            // 49
            "sacrifice",       // 50
            "relat",           // 51
            "fetter",          // 52
            "pinkcloud",       // 53
            "antil",           // 54
            "bloodinv",        // 55
            "potion_rat",      // 56
        };

        private static readonly Dictionary<string, int> ById = BuildIndex();

        private static Dictionary<string, int> BuildIndex()
        {
            var map = new Dictionary<string, int>(IdsByFrame.Length, StringComparer.Ordinal);
            for (int p = 0; p < IdsByFrame.Length; p++)
            {
                // p is frame p + 2; a sprite index is frame - 1.
                map[IdsByFrame[p]] = p + 1;
            }

            return map;
        }

        /// <summary>
        /// Sprite index for an effect id, or <see cref="Blank"/> when the id has no frame.
        ///
        /// <para><b>Blank rather than -1, because that is what the oracle does.</b> An unknown id throws
        /// inside <c>gotoAndStop</c> and the <c>catch</c> writes frame 1 — an empty slot, not a hidden
        /// one. Returning "hidden" here would make a data-driven effect silently disappear from the
        /// strip, which is a worse failure than an empty icon: nothing on screen would say an effect is
        /// running.</para>
        /// </summary>
        public static int IndexOf(string effectId)
        {
            if (string.IsNullOrEmpty(effectId))
            {
                return Blank;
            }

            return ById.TryGetValue(effectId, out int index) ? index : Blank;
        }

        /// <summary>True when the id has its own frame — i.e. it will not draw as the blank fallback.</summary>
        public static bool IsKnown(string effectId)
        {
            return !string.IsNullOrEmpty(effectId) && ById.ContainsKey(effectId);
        }

        /// <summary>The id a frame draws, for diagnostics; <c>null</c> for the blank frame.</summary>
        public static string IdAt(int spriteIndex)
        {
            int position = spriteIndex - 1;
            return position >= 0 && position < IdsByFrame.Length ? IdsByFrame[position] : null;
        }
    }
}
