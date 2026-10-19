using System.Globalization;
using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// The "lift shaft" half of AS3 <c>Area</c>: an area may carry <c>grav</c>, and while it is enabled
    /// it writes that value into every tile it covers (<c>Area.setLift</c>, <c>Area.as:386-400</c>).
    /// A negative value makes the tile push units <b>up</b>, which is how the nio shaft, the plant's
    /// jump pads and the encl/medbay lifts work in the original.
    ///
    /// <para><b>Why this is a tile write and not a trigger.</b> Nothing in the oracle detects a unit
    /// "entering" a lift. <c>Area.setLift</c> stamps <c>Tile.grav</c> once, and from then on
    /// <c>Unit.forces()</c> reads the tile under the unit's waist
    /// (<c>Unit.as:1960-1966</c>, <c>UnitPlayer.as:766-788</c>) and multiplies gravity by it. The
    /// shaft therefore lifts the player, every enemy and every prop-carrying body that stands in it,
    /// whether or not a trigger collider is anywhere near them. This type reproduces exactly that:
    /// the <c>on</c>/<c>off</c> commands only rewrite the tiles.</para>
    ///
    /// <para><b>Which state the area is in.</b> AS3 <c>Area.enabled</c> is initialised from
    /// <c>@off</c> (<c>Area.as:120-123</c> — <c>off="1"</c> means <i>disabled</i>) and then toggled by
    /// <c>command("on"/"off"/"onoff")</c> (<c>:257-283</c>). The port already carries that flag as
    /// <see cref="ObjectInstance.isActive"/> (it is what <c>AreaTriggerPresenter</c> gates the trigger
    /// collider on), so this type reads and writes the same field rather than adding a second one.
    /// </para>
    ///
    /// <para><b>Measured scope.</b> 28 area placements across the 640 imported rooms author
    /// <c>grav</c>: 21 negative (shafts −0.5, the <c>lift</c> room −2.3, the <c>jump</c>/<c>конец</c>
    /// pads −10), 2 positive (<c>grav</c> 0.2, <c>w_grif</c> 0.3), and 7 start disabled with
    /// <c>off="1"</c> (nio's <c>nioLift1</c>, <c>room_4_0</c>, <c>sentry</c>, the two
    /// <c>лаборатория</c> columns and the two <c>мед.блок</c> columns).</para>
    /// </summary>
    public static class AreaLift
    {
        /// <summary>AS3's "no effect" value: <c>Tile.grav</c> defaults to 1 (<c>Tile.as:72</c>).</summary>
        public const float Neutral = TileData.DefaultGravity;

        /// <summary>
        /// AS3 <c>Area.rx</c>/<c>ry</c> default to 2 (<c>Area.as:19-21</c>), so an area that authors
        /// neither <c>w</c> nor <c>h</c> covers a 2×2 block.
        /// </summary>
        private const float DefaultSpanTiles = 2f;

        /// <summary>
        /// Whether this object is an area that changes tile gravity at all.
        ///
        /// <para><b>Only <c>grav == 1</c> is neutral.</b> <c>grav="0"</c> is meaningful — it removes
        /// gravity entirely (<c>UnitPlayer.forces</c> takes neither the <c>&gt; 0</c> nor the
        /// <c>&lt; 0</c> arm and leaves <c>dy</c> alone) — so it must not be treated as "no lift".
        /// No shipped area authors it, but the distinction is the oracle's.</para>
        /// </summary>
        public static bool IsLiftArea(ObjectInstance area)
        {
            if (area == null) return false;
            if (!string.Equals(area.objectType, "area", System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !Mathf.Approximately(ResolveLift(area), Neutral);
        }

        /// <summary>
        /// AS3 <c>Area.lift</c> — the <c>@grav</c> value, or 1 when the attribute is absent
        /// (<c>Area.as:198-201</c>, and the field's own initialiser at <c>:47</c>).
        /// </summary>
        public static float ResolveLift(ObjectInstance area)
        {
            if (area == null) return Neutral;

            return TryParseFloat(area.GetAttribute("grav", string.Empty), out float lift)
                ? lift
                : Neutral;
        }

        /// <summary>
        /// AS3 <c>Area.enabled</c>'s initial value: <c>if(@off.length()) enabled = @off &lt;= 0;</c>
        /// (<c>Area.as:120-123</c>). An area with no <c>off</c> attribute starts <b>enabled</b>, which
        /// is why most lift shafts in the game work without any button.
        /// </summary>
        public static bool ResolveInitialEnabled(ObjectInstance area)
        {
            if (area == null) return true;

            // `@off.length()` is an attribute-presence test, not a value test: `off="0"` is present
            // and therefore *enables* (0 <= 0), while an absent attribute leaves the default true.
            if (!TryParseFloat(area.GetAttribute("off", string.Empty), out float off))
            {
                return true;
            }

            return off <= 0f;
        }

        /// <summary>
        /// The tile rectangle an area covers, in the port's bottom-up tile space.
        ///
        /// <para><b>The AS3 rect is <c>[bx, bx+rx-1] × [by-ry+1, by]</c> with Y running down</b>
        /// (<c>Area.setLift</c>, <c>Area.as:388-399</c>). <see cref="ObjectInstance.position"/> was
        /// imported as the <i>bottom-left</i> corner of the same box in the port's up-positive space
        /// (<c>RoomPopulator.CreateArea</c> → <c>WorldCoordinates.TileToPixel</c> of the flipped row),
        /// so the rectangle is simply <c>position</c> plus <c>w×h</c> tiles — the same rect
        /// <c>AreaTriggerSystem.GetTriggerPixelRect</c> draws and tests against.</para>
        /// </summary>
        public static RectInt GetTileRect(ObjectInstance area)
        {
            if (area == null) return new RectInt(0, 0, 0, 0);

            float tile = WorldConstants.TILE_SIZE;

            // The epsilon is the motor's own convention (`+ 0.01f`) and only guards float division at
            // a tile boundary. `position` is tile-aligned by construction, so this is exact today.
            int x = Mathf.FloorToInt(area.position.x / tile + 0.01f);
            int y = Mathf.FloorToInt(area.position.y / tile + 0.01f);

            float spanX = TryParseFloat(area.GetAttribute("w", string.Empty), out float w) ? w : DefaultSpanTiles;
            float spanY = TryParseFloat(area.GetAttribute("h", string.Empty), out float h) ? h : DefaultSpanTiles;

            int width = Mathf.Max(1, Mathf.RoundToInt(spanX));
            int height = Mathf.Max(1, Mathf.RoundToInt(spanY));

            return new RectInt(x, y, width, height);
        }

        /// <summary>
        /// Stamps (or clears) this area's gravity on the tiles it covers — AS3 <c>Area.setLift()</c>.
        /// </summary>
        /// <param name="enabled">
        /// The area's <c>enabled</c> state. Disabled writes <see cref="Neutral"/> back, which is the
        /// oracle's own restore path (<c>tile.grav = enabled ? lift : 1</c>, <c>Area.as:395</c>).
        /// </param>
        /// <returns>How many tiles were written. Zero means "not a lift area, or no tiles there".</returns>
        public static int Apply(RoomInstance room, ObjectInstance area, bool enabled)
        {
            if (room == null || area == null) return 0;

            // `IsLiftArea` rather than a bare `ResolveLift` comparison: it is the same question a
            // presenter or a debug overlay asks, and having one predicate means "is this a lift?" is
            // answered in exactly one place. It costs a second read of one attribute, on a path that
            // runs once per area per room build and once per button press — not per frame.
            if (!IsLiftArea(area))
            {
                return 0;
            }

            float lift = ResolveLift(area);

            RectInt rect = GetTileRect(area);
            float value = enabled ? lift : Neutral;
            int written = 0;

            for (int x = rect.xMin; x < rect.xMax; x++)
            {
                for (int y = rect.yMin; y < rect.yMax; y++)
                {
                    TileData tile = room.GetTileAtCoord(new Vector2Int(x, y));
                    if (tile == null) continue;

                    tile.gravityMultiplier = value;
                    written++;
                }
            }

            return written;
        }

        /// <summary>
        /// <see cref="Apply"/> using the area's current <see cref="ObjectInstance.isActive"/> — the
        /// shape every <c>on</c>/<c>off</c>/<c>onoff</c> command needs after it has toggled the flag.
        /// </summary>
        public static void ApplyCurrent(RoomInstance room, ObjectInstance area)
        {
            if (area == null) return;
            Apply(room, area, area.isActive);
        }

        /// <summary>
        /// AS3 <c>Area.command</c>'s unconditional tail: <c>if(lift != 1) setLift();</c>
        /// (<c>Area.as:271-274</c>). Bound as <c>ObjectInteractionBroadcast.AreaPresentation</c> so
        /// the broadcast <c>allact</c> path refreshes a lift exactly like the <c>@targ</c> path does.
        /// </summary>
        public static void RefreshPresentation(RoomInstance room, ObjectInstance area)
        {
            ApplyCurrent(room, area);
        }

        private static bool TryParseFloat(string raw, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            return float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
