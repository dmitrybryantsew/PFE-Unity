using System;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// A unit's collision box in <b>world pixels</b> — standing and crouched.
    ///
    /// <para>Four numbers, because the oracle keeps four: <c>sX</c>/<c>sY</c> for standing and
    /// <c>sitX</c>/<c>sitY</c> for crouched (<c>Unit.as:1020-1048</c>). The port's
    /// <c>UnitDefinition</c> stores them in Unity units; this record is the pixel form the collision
    /// code actually uses, so the conversion happens once instead of at every call site.</para>
    /// </summary>
    public readonly struct UnitBodyBox
    {
        /// <summary>Standing width, world pixels (AS3 <c>stayX</c> / <c>scX</c>).</summary>
        public readonly float WidthPx;

        /// <summary>Standing height, world pixels (AS3 <c>stayY</c> / <c>scY</c>).</summary>
        public readonly float HeightPx;

        /// <summary>Crouched width, world pixels (AS3 <c>sitX</c>).</summary>
        public readonly float SitWidthPx;

        /// <summary>Crouched height, world pixels (AS3 <c>sitY</c>).</summary>
        public readonly float SitHeightPx;

        public UnitBodyBox(float widthPx, float heightPx, float sitWidthPx, float sitHeightPx)
        {
            WidthPx = widthPx;
            HeightPx = heightPx;
            SitWidthPx = sitWidthPx;
            SitHeightPx = sitHeightPx;
        }

        public override string ToString() =>
            $"UnitBodyBox({WidthPx:F0}x{HeightPx:F0} sit {SitWidthPx:F0}x{SitHeightPx:F0})";
    }

    /// <summary>
    /// Resolves a unit's body box from its authored size, applying the oracle's two crouch defaults.
    ///
    /// <para><b>Why this is a rule and not four fields.</b> The oracle does not store the crouched box;
    /// it <i>derives</i> it when the data omits it (<c>Unit.as:1030-1048</c>):</para>
    /// <code>
    /// sitX = node.@sitX.length() ? node.@sitX : stayX;      // width unchanged
    /// sitY = node.@sitY.length() ? node.@sitY : stayY / 2;  // height halved
    /// </code>
    /// <para>A port that stores only the standing size and lets each consumer guess the crouch will
    /// disagree with the oracle — and, once there are two peers, with <i>itself</i> if the two ends
    /// resolve it differently. Both halves of row 0.3's acceptance are the same requirement: the box is
    /// a function of content, computed by one shared rule, so every peer reads the same numbers.</para>
    ///
    /// <para><b>Zero means "not authored", not "zero tall".</b> A crouched height of 0 px is not a
    /// legal box, so it is the natural sentinel for "the data omitted <c>sitY</c>". That matters because
    /// <c>UnitDefinition.sitHeight</c> ships with a non-zero default (0.5) — see the note on
    /// <see cref="Resolve"/>.</para>
    /// </summary>
    public static class UnitBodyBoxRules
    {
        /// <summary>Unity units → world pixels, matching <c>TileQueryConstants.UnitToPixel</c>. Repeated
        /// here so the rule stays Unity-free and offline-executable; the fixture pins the two equal.</summary>
        public const float PixelsPerUnit = 100f;

        /// <summary>
        /// Resolve the box from authored values, in <b>world pixels</b>.
        ///
        /// <para><b>The <c>UnitDefinition.sitHeight</c> default is a trap this rule cannot fix from
        /// here.</b> The data class defaults it to <c>0.5</c>, not to <c>0</c>, so "the author omitted
        /// <c>sitY</c>" is <i>not</i> representable in the serialized asset: an unauthored unit crouches
        /// to 50 px whatever its height. The importer compensates by always writing <c>sitY/2</c> when
        /// the attribute is absent (<c>UnitDataImporter.ParsePhysics</c>), which is why this rule's
        /// <c>&lt;= 0</c> branch is a fallback for hand-authored data rather than the normal path. Do not
        /// read the <c>&lt;= 0</c> branch as "the oracle's absence test" — the importer already applied
        /// it, and a unit whose <c>sitHeight</c> is 0 here has been authored wrong.</para>
        /// </summary>
        /// <param name="stayWidthPx">Authored standing width (AS3 <c>sX</c>), world pixels.</param>
        /// <param name="stayHeightPx">Authored standing height (AS3 <c>sY</c>), world pixels.</param>
        /// <param name="sitWidthPx">Authored crouched width (AS3 <c>sitX</c>), world pixels; 0 = absent.</param>
        /// <param name="sitHeightPx">Authored crouched height (AS3 <c>sitY</c>), world pixels; 0 = absent.</param>
        public static UnitBodyBox Resolve(
            float stayWidthPx, float stayHeightPx, float sitWidthPx = 0f, float sitHeightPx = 0f)
        {
            if (stayWidthPx <= 0f)
                throw new ArgumentOutOfRangeException(nameof(stayWidthPx), stayWidthPx,
                    "A unit with no standing width has no body box; the oracle's sX is mandatory " +
                    "(Unit.as:1020-1027 only guards the attribute's presence, never a zero).");

            if (stayHeightPx <= 0f)
                throw new ArgumentOutOfRangeException(nameof(stayHeightPx), stayHeightPx,
                    "A unit with no standing height has no body box; the oracle's sY is mandatory.");

            float sitWidth = sitWidthPx > 0f ? sitWidthPx : stayWidthPx;
            float sitHeight = sitHeightPx > 0f ? sitHeightPx : stayHeightPx / 2f;

            return new UnitBodyBox(stayWidthPx, stayHeightPx, sitWidth, sitHeight);
        }

        /// <summary>
        /// Resolve from the <c>UnitDefinition</c> form — Unity units, with <c>sitHeight &lt;= 0</c>
        /// meaning "derive it". Kept separate from <see cref="Resolve"/> so the pixel form stays the one
        /// arithmetic the fixture pins, and the unit conversion happens at exactly one boundary.
        /// </summary>
        public static UnitBodyBox ResolveFromWorldUnits(
            float stayWidthUnits, float stayHeightUnits, float sitHeightUnits)
        {
            return Resolve(
                stayWidthUnits * PixelsPerUnit,
                stayHeightUnits * PixelsPerUnit,
                0f,
                sitHeightUnits * PixelsPerUnit);
        }
    }
}
