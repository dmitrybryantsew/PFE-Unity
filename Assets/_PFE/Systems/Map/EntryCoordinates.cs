using System.Globalization;
using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// The <c>"x:y"</c> an entry override carries, parsed into a room position.
    ///
    /// <para><b>Oracle.</b> AS3 stores the string on <c>Game.curCoord</c> (<c>Game.as:438</c>, set by
    /// <c>gotoLand(param1, param2)</c>) and hands it to <c>Land.enterLand</c>
    /// (<c>Game.as:371</c>), which splits it on <c>":"</c> into <c>locX</c>/<c>locY</c> — the land's own
    /// cell coordinate (<c>Land.as:1148-1167</c>). It is produced in exactly one place: the
    /// <c>gotoland</c> script branch with <c>n == 1</c>, which builds <c>opt1 + ":" + opt2</c>
    /// (<c>Script.as:449-452</c>). One live example exists in the data —
    /// <c>&lt;s act="gotoland" val="raiders" n="1" opt1="2" opt2="2"/&gt;</c> (<c>RoomsProb.as:2562</c>).</para>
    ///
    /// <para><b>Why the number passes straight through to a room position.</b> The port's
    /// <c>RoomTemplate.fixedPosition</c> is the room's position in the room grid, which is the same
    /// number as the land cell an authored room sits in — that is why
    /// <c>MapBridge.ResolveEntrancePosition</c> can return a <c>beg0</c> template's
    /// <c>fixedPosition</c> directly as the entry position. So for the authored lands the two agree and
    /// no conversion is needed. A procedural land would need the cell→room mapping that L1's
    /// <c>WorldBuilder.BuildLand</c> will own; until then a coordinate override is only meaningful for
    /// an authored destination, which is the only kind the one live caller has.</para>
    ///
    /// <para><b>Extracted from <c>MapBridge</c> on purpose.</b> A parse that silently returns "no
    /// override" for input it did not understand puts the player at the land's default entry with no
    /// complaint, and the only way to prove the difference is a positive control — which needs the
    /// parser reachable from a test without a <c>MonoBehaviour</c> (constructing one is an ECall and
    /// cannot run offline).</para>
    /// </summary>
    public static class EntryCoordinates
    {
        /// <summary>
        /// Parses <paramref name="coordinates"/> into a room position.
        ///
        /// <para>The shape follows AS3 rather than a stricter reading: a missing <c>y</c> is <c>0</c>
        /// (<c>Land.as:1159-1166</c> treats a one-part split as <c>locY = 0</c>), and anything past the
        /// second part is ignored. A part that is present but <b>not</b> a number fails the whole parse
        /// — AS3 would carry the garbage through as a string, which the port cannot do and must not
        /// pretend to.</para>
        /// </summary>
        /// <returns><c>false</c> for null, blank, or non-numeric input.</returns>
        public static bool TryParse(string coordinates, out Vector3Int position)
        {
            position = default;

            if (string.IsNullOrWhiteSpace(coordinates)) return false;

            string[] parts = coordinates.Split(':');

            if (!TryParsePart(parts[0], out int x)) return false;

            // AS3: `if(_loc3_.length >= 2) locY = _loc3_[1]; else locY = 0;`
            int y = 0;
            if (parts.Length >= 2 && !TryParsePart(parts[1], out y)) return false;

            position = new Vector3Int(x, y, 0);
            return true;
        }

        /// <summary>
        /// <see cref="TryParse"/> without the out parameter. <c>null</c> means "no override" — the
        /// caller then uses the land's own entry cell.
        /// </summary>
        public static Vector3Int? Parse(string coordinates)
        {
            return TryParse(coordinates, out Vector3Int position) ? position : (Vector3Int?)null;
        }

        /// <summary>
        /// Invariant culture and <see cref="NumberStyles.Integer"/> rather than
        /// <c>int.Parse</c>'s defaults: a locale that groups digits or uses a non-ASCII minus would
        /// otherwise accept or reject different strings on different machines, and a data-authored
        /// coordinate is not locale-dependent.
        /// </summary>
        private static bool TryParsePart(string part, out int value)
        {
            return int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }
}
