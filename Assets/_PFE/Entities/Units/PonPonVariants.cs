using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// Picks the generic villager pony's look from the placement's <c>tr</c> — the port of
    /// <c>UnitPonPon.as:112 vis.osn.pon.gotoAndStop(this.tr)</c>.
    ///
    /// <para><b>What was wrong.</b> Every <c>ponpon</c> in the game is one unit row
    /// (<c>AllData.as:56</c>) whose <c>&lt;vis&gt;</c> names <c>visualPonPon</c>, and that symbol's
    /// own timeline is <b>one frame</b>: it places <c>osn</c>, which places <c>pon</c>, and <c>pon</c>
    /// is the real artwork — <b>27 frames</b>, one per villager. The oracle addresses it by number
    /// (<c>gotoAndStop(tr)</c>); the importer only ever exported the outer symbol's single frame, so
    /// the port drew frame 1 of <c>pon</c> for every villager in the wasteland. That is the "it is
    /// just one mare" report.</para>
    ///
    /// <para><b>Why the numbers are constants and not derived.</b> The composite transform is the
    /// oracle's own data and is fixed: <c>visualPonPon</c> places <c>osn</c> at <c>(-3, -34)</c> with
    /// no scale, and <c>osn</c> places <c>pon</c> at <c>scale 0.5463, translate (0.1, -5.5)</c>. So a
    /// <c>pon</c> frame lands at <c>0.5463 × p + (-2.9, -39.5)</c> relative to the unit origin, and the
    /// origin sits <b>8.54 px</b> above the frame's bottom edge — measured from the SWF's own placement
    /// matrices and shape bounds, and cross-checked against the composite PNG
    /// (<c>visualPonPon/f001.png</c>, 100×98, anchor 4.67 px above its bottom: 8.54 × 0.5463 = 4.66).</para>
    ///
    /// <para><b>Nothing here draws.</b> <see cref="TryResolve"/> answers "which sprite, at what scale,
    /// with what feet gap"; <c>RoomUnitSpawner</c> applies it, so the resting frame and the animation
    /// path cannot disagree about where the pony stands.</para>
    /// </summary>
    public static class PonPonVariants
    {
        /// <summary>The one unit row every villager shares (<c>AllData.as:56</c>).</summary>
        public const string UnitId = "ponpon";

        /// <summary>The placement attribute that selects the look — AS3 <c>UnitPonPon.tr</c>.</summary>
        public const string Attribute = "tr";

        /// <summary>First usable frame of <c>pon</c>. Frame 1 is a villager, not a spacer.</summary>
        public const int FirstVariant = 1;

        /// <summary>
        /// Frames on <c>pon</c>'s timeline. 27, which is also the top of the <c>stabpon</c> roll
        /// (<c>UnitPonPon.as:40</c> <c>random * 33 + 1</c> is bounded by the clip, not by the roll).
        /// </summary>
        public const int VariantCount = 27;

        /// <summary>
        /// The <c>osn</c> → <c>pon</c> placement scale, per axis — the SWF stores them separately and
        /// they are not quite equal (<c>0.54629517</c> / <c>0.54663086</c>). Applied as-is so a frame
        /// lands at the size the oracle draws it.
        /// </summary>
        public static readonly Vector2 Scale = new Vector2(0.54629517f, 0.54663086f);

        /// <summary>
        /// How far the unit's origin sits above a <c>pon</c> frame's bottom edge, in <b>source</b>
        /// pixels — 8.54, which the scale turns into the 4.67 px the composite frame carries.
        /// </summary>
        public const float FeetGapPixels = 8.54f;

        /// <summary>
        /// The frame a placement's <c>tr</c> selects, 1-based, clamped into the clip.
        ///
        /// <para><b>A missing <c>tr</c> falls back to frame 1 rather than rolling.</b> The oracle rolls
        /// <c>floor(random * 15 + 13)</c> in that case (<c>UnitPonPon.as:59</c>), but the roll happens
        /// in the unit's constructor <i>after</i> the base class's facing coin flip, and both draw from
        /// the shared spawn stream (<c>Location.as:1185</c> → <c>Unit.as:609-613</c>). Adding it here
        /// would shift every later placement in every room — a silent whole-map change for a cosmetic
        /// gain. Frame 1 is the honest "the room named nothing" answer, and every shipped <c>ponpon</c>
        /// placement authors a <c>tr</c>.</para>
        /// </summary>
        public static int ResolveFrame(string tr)
        {
            if (string.IsNullOrWhiteSpace(tr) ||
                !int.TryParse(tr.Trim(), System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out int value))
            {
                return FirstVariant;
            }

            if (value < FirstVariant)
            {
                return FirstVariant;
            }

            return value > FirstVariant + VariantCount - 1 ? FirstVariant + VariantCount - 1 : value;
        }

        /// <summary>
        /// The array slot <paramref name="tr"/> selects out of a clip of <paramref name="frameCount"/>
        /// frames, or <b>-1</b> when the clip cannot satisfy it.
        ///
        /// <para><b>Why this is split out of <see cref="TryResolve"/>.</b> Everything that decides
        /// <i>which</i> pony a villager is — the clamp and the off-by-one between a 1-based frame number
        /// and a 0-based array slot — is integer arithmetic with no Unity type in sight, so it can be
        /// executed by an offline fixture. <see cref="TryResolve"/> keeps only the part that genuinely
        /// needs a <see cref="Sprite"/>. A rule that can only be tested inside the editor is a rule that
        /// is tested when someone opens the editor.</para>
        /// </summary>
        public static int ResolveIndex(string tr, int frameCount)
        {
            if (frameCount <= 0)
            {
                return -1;
            }

            int index = ResolveFrame(tr) - FirstVariant;
            return index < frameCount ? index : -1;
        }

        /// <summary>
        /// The sprite, scale and feet gap to draw for <paramref name="tr"/>, or false when this unit is
        /// not a villager or the variant art has not been imported.
        /// </summary>
        /// <remarks>
        /// <para><b>False is a real answer, not an error.</b> The variant frames are written by
        /// <c>UnitSpriteImporter</c>'s villager pass; until that has run,
        /// <see cref="UnitDefinition.variantSprites"/> is empty and the caller keeps drawing
        /// <see cref="UnitDefinition.sprite"/>. The room then shows one mare, exactly as it did
        /// before, instead of a unit with no art at all.</para>
        /// </remarks>
        public static bool TryResolve(UnitDefinition definition, string tr,
                                      out Sprite sprite, out Vector2 scale, out float feetGapPixels)
        {
            sprite = null;
            scale = Vector2.one;
            feetGapPixels = 0f;

            if (definition == null || definition.variantSprites == null ||
                definition.variantSprites.Length == 0)
            {
                return false;
            }

            int index = ResolveIndex(tr, definition.variantSprites.Length);
            if (index < 0)
            {
                return false;
            }

            Sprite variant = definition.variantSprites[index];
            if (variant == null)
            {
                return false;
            }

            sprite = variant;
            scale = Scale;
            feetGapPixels = FeetGapPixels;
            return true;
        }
    }
}
