using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// The <c>&lt;move&gt;</c> child node of a placed <c>&lt;obj&gt;</c> — the data that makes an object
    /// a moving platform.
    ///
    /// <para><b>This is a child ELEMENT, not an attribute, and that is why the port had none of it.</b>
    /// The oracle reads <c>this.xml.move.@dx</c> and friends (<c>Interact.as:427-468</c>), where
    /// <c>this.xml</c> is the placed node — so the authored shape is</para>
    /// <code>
    /// &lt;obj id="platform2" code="nfVdIexR7Ke3YmcK" x="9" y="5" allid="d2"&gt;
    ///   &lt;move dx="14" tstay="30" tmove="180"/&gt;
    /// &lt;/obj&gt;
    /// </code>
    /// <para><c>AS3RoomParser.ParseObject</c> copies only <c>objNode.Attributes</c>
    /// (<c>AS3RoomParser.cs:348-352</c>), so the node was dropped at import and no ported room asset
    /// carries it. The importer now flattens it into <c>move_dx</c> / <c>move_dy</c> /
    /// <c>move_tstay</c> / <c>move_tmove</c> / <c>move_on</c>; the 97 rooms that author one still need a
    /// re-import before this type sees anything.</para>
    ///
    /// <para><b>The measured population.</b> 73 platform move nodes across the shipped rooms: 35 on
    /// <c>platform2</c>, 25 on <c>wturret</c> (the rail-mounted turret), 6 on <c>knop2</c> (a button that
    /// pops out and auto-closes), 5 on <c>platform3</c>, and one each on <c>p_luna</c> and
    /// <c>wallsafe</c>. Every one of them carries <c>tstay</c> and <c>tmove</c>; 6 carry <c>on="1"</c>
    /// and therefore start moving the moment the room loads.</para>
    /// </summary>
    public readonly struct MapObjectMoveData
    {
        /// <summary>Horizontal travel in <b>tiles</b> — AS3 <c>@dx</c>, multiplied by <c>World.tileX</c>.</summary>
        public readonly float Dx;

        /// <summary>Vertical travel in <b>tiles</b> — AS3 <c>@dy</c>, multiplied by <c>World.tileY</c>.</summary>
        public readonly float Dy;

        /// <summary>Whether <c>@dx</c> was authored at all. Absent means "this axis does not move", which is different from <c>dx="0"</c> only in intent — AS3 writes <c>endX = endX2 = X</c> for the absent case and <c>X + 0</c> for the zero case, and the two agree.</summary>
        public readonly bool HasDx;

        /// <summary>Whether <c>@dy</c> was authored at all. See <see cref="HasDx"/>.</summary>
        public readonly bool HasDy;

        /// <summary>Frames parked at each end — AS3 <c>@tstay</c>, default 10.</summary>
        public readonly int TStay;

        /// <summary>Frames spent travelling each way — AS3 <c>@tmove</c>, default 100.</summary>
        public readonly int TMove;

        /// <summary>
        /// Whether <c>@on</c> was authored — AS3 sets <c>moveSt = 4</c>, i.e. "already moving, forever"
        /// (<c>Interact.as:465-467</c>). 6 of the 73 nodes in the shipped rooms do this.
        /// </summary>
        public readonly bool StartsMoving;

        /// <summary>
        /// AS3 <c>loc.mirror</c>, which flips the sign of <c>dx</c> (<c>Interact.as:434-441</c>).
        ///
        /// <para><b>Always false today, and that is a fact about the port rather than about the data.</b>
        /// No <see cref="RoomInstance"/> carries a mirror flag — the port never models the mirrored room
        /// variant AS3 builds for a reflected land — so there is nothing to read it from. It is a field
        /// rather than a hardcoded <c>false</c> so that the sign rule lives in one place when a mirrored
        /// room does appear, and so a fixture can pin the flipped arm without inventing a second
        /// code path.</para>
        /// </summary>
        public readonly bool Mirror;

        /// <summary>
        /// For a fixture that needs to pin the mirrored arm — the importer and the accessor are the only
        /// production constructors.
        /// </summary>
        public MapObjectMoveData(
            float dx, float dy, bool hasDx, bool hasDy, int tStay, int tMove, bool startsMoving, bool mirror)
        {
            Dx = dx;
            Dy = dy;
            HasDx = hasDx;
            HasDy = hasDy;
            TStay = tStay;
            TMove = tMove;
            StartsMoving = startsMoving;
            Mirror = mirror;
        }

        /// <summary>
        /// Build from the flattened attributes. <paramref name="present"/> is false when the placement
        /// authors no <c>move</c> node at all, in which case the returned value is meaningless and the
        /// caller must not use it.
        /// </summary>
        public static MapObjectMoveData FromAttributes(
            System.Func<string, string, string> read,
            bool mirror,
            out bool present)
        {
            string rawDx = read("move_dx", string.Empty);
            string rawDy = read("move_dy", string.Empty);
            string rawStay = read("move_tstay", string.Empty);
            string rawMove = read("move_tmove", string.Empty);
            string rawOn = read("move_on", string.Empty);

            // Presence is "the node was there", which the importer records as `move_present`. Falling
            // back to "any of the children is non-empty" keeps a hand-authored fixture working without
            // it. The node with no attributes at all (`<move/>`) is legal in AS3 — it sets isMove,
            // pins endX/endY to the start, and therefore never actually moves — and only the marker
            // distinguishes it from "no node".
            present = string.Equals(read("move_present", string.Empty), "1", System.StringComparison.Ordinal)
                      || !string.IsNullOrEmpty(rawDx)
                      || !string.IsNullOrEmpty(rawDy)
                      || !string.IsNullOrEmpty(rawStay)
                      || !string.IsNullOrEmpty(rawMove)
                      || !string.IsNullOrEmpty(rawOn);

            // Defaults are AS3's field initialisers: tStay = 10, tMove = 100 (Interact.as:146-148).
            int tStay = ParseInt(rawStay, 10);
            int tMove = ParseInt(rawMove, 100);

            return new MapObjectMoveData(
                dx: ParseFloat(rawDx, 0f),
                dy: ParseFloat(rawDy, 0f),
                hasDx: !string.IsNullOrEmpty(rawDx),
                hasDy: !string.IsNullOrEmpty(rawDy),
                tStay: tStay,
                tMove: tMove,
                startsMoving: !string.IsNullOrEmpty(rawOn),
                mirror: mirror);
        }

        private static float ParseFloat(string raw, float fallback)
        {
            return float.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float value)
                ? value
                : fallback;
        }

        private static int ParseInt(string raw, int fallback)
        {
            return int.TryParse(
                raw,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int value)
                ? value
                : fallback;
        }
    }
}
