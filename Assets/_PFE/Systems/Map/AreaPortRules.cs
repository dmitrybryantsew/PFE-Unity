using System;
using System.Globalization;

namespace PFE.Systems.Map
{
    /// <summary>
    /// The teleport half of an AS3 <c>Area</c> — <c>Area.as:289-434</c> — as pure functions.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists at all.</b> <c>bossnecr1</c> (Mchavi, the zebra necromancer of Canterlot)
    /// is built around eight of these: four on the left wall, four on the right, each with
    /// <c>port="x:y"</c> and <c>tip="en"</c>. Walk into one and the boss is moved to the other side of the
    /// arena. The port imported every attribute (<c>bossnecr1.asset</c> carries all eight areas with
    /// <c>port</c>, <c>vis</c>, <c>tip</c>, <c>w</c> and <c>h</c>) and <b>nothing read <c>port</c></b>, so
    /// the fight had no teleports and the walls had no portals — the reported symptom, exactly.</para>
    ///
    /// <para><b>Three rules, each with a wrong answer that looks right.</b></para>
    /// <list type="bullet">
    /// <item><description><b>Who activates it.</b> <c>tip == "gg"</c> (or absent) means the player;
    /// anything else means "the first unit whose own <c>areaTestTip</c> equals this" — for the necromancer
    /// that is <c>"en"</c>, set in her constructor (<c>UnitBossNecr.as:82</c>). Treating every unit as an
    /// activator is the bug that once fired room transitions off a wandering NPC, so the match is
    /// <i>exact and non-empty</i>: an area with <c>tip=""</c> activates for nobody
    /// (<c>Area.as:290-293</c> returns early).</description></item>
    /// <item><description><b>Where the rectangle is — and where it is not.</b> AS3 measures a room's Y
    /// <b>down</b> from the ceiling and anchors an area at its <i>bottom</i> row:
    /// <c>Y2 = by*tile + tile</c>, <c>Y1 = Y2 - ry*tile</c> (<c>:72-79</c>). That mirror has
    /// <b>already been applied at import</b>: <c>RoomPopulator.CreateArea</c> stores
    /// <c>position = WorldCoordinates.TileToPixel</c> of the flipped row, so the placement rectangle is
    /// plain <c>position</c> plus <c>w×h</c> tiles in the port's up-positive space — the same rect
    /// <c>AreaTriggerSystem.GetTriggerPixelRect</c> already draws and tests. <b>No rule is needed here,
    /// and adding one is how the drawn rectangle and the tested rectangle drift apart.</b></description></item>
    ///
    /// <item><description><b>The destination is the one thing that still needs the mirror.</b>
    /// <c>@port</c> is a map attribute, copied verbatim by the importer, so it stays in AS3's
    /// down-positive rows — <c>Area.teleport</c> feeds it straight into <c>unit.teleport(x, y)</c>
    /// (<c>:426</c>). That is the vertical flip that once teleported the alicorn boss into the floor
    /// (<c>BossAlicornRules.MovePointPixelY</c>), and it is why
    /// <see cref="PortDestinationPixels"/> takes the room's height and border while the area rectangle
    /// takes neither.</description></item>
    /// <item><description><b>When it fires.</b> On the <i>rising edge</i> only:
    /// <c>if(active &amp;&amp; !preactive &amp;&amp; onPort)</c> (<c>:341-344</c>). A poll that fires whenever
    /// the unit is inside would teleport it again on the next frame if the destination happened to be
    /// inside another port area.</description></item>
    /// </list>
    ///
    /// <para><b>Unity-free on purpose.</b> No <c>UnityEngine</c> type appears in any signature, so an
    /// offline harness can link this file as source and pin it against the oracle's arithmetic without a
    /// scene — the same contract <see cref="RoomStateRules"/> and <c>LootFallStepRules</c> hold to.</para>
    /// </remarks>
    public static class AreaPortRules
    {
        /// <summary>
        /// AS3's default <c>tip</c> (<c>Area.as:17</c>) — the player is the activator.
        /// </summary>
        public const string PlayerTip = "gg";

        /// <summary>
        /// AS3's default area size in tiles: <c>rx = 2</c>, <c>ry = 2</c> (<c>Area.as:24-25</c>).
        /// <c>@w</c> and <c>@h</c> override them one at a time (<c>:66-80</c>).
        /// </summary>
        /// <remarks>
        /// The rectangle itself is built by <c>AreaTriggerSystem.GetTriggerPixelRect</c>, which applies
        /// this same default for an area that authors neither attribute. The constant is kept here
        /// because it is an oracle fact about <c>Area</c>, not a detail of one caller.
        /// </remarks>
        public const int DefaultAreaTiles = 2;

        /// <summary>
        /// Read <c>@port</c> — <c>"x:y"</c> in tiles (<c>Area.as:212-224</c>).
        /// </summary>
        /// <remarks>
        /// The oracle requires only that the split produce <b>at least two</b> parts and coerces both
        /// through E4X; a malformed value would then become <c>NaN</c> and teleport the unit to nowhere.
        /// Here a malformed value is a <b>refusal</b>, because "no port" is a state the caller already
        /// handles and "teleport to NaN" is not.
        /// </remarks>
        public static bool TryParsePort(string raw, out int portTileX, out int portTileY)
        {
            portTileX = -1;
            portTileY = -1;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            string[] parts = raw.Split(':');
            if (parts.Length < 2)
            {
                return false;
            }

            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
            {
                return false;
            }

            portTileX = x;
            portTileY = y;
            return true;
        }

        /// <summary>
        /// Whether a unit carrying <paramref name="unitTip"/> is this area's activator.
        /// </summary>
        /// <remarks>
        /// <b>An empty area tip activates for nobody</b> — <c>Area.step()</c> returns before the scan when
        /// <c>tip == ""</c> (<c>:290-293</c>), and 31 authored areas rely on that. A blank <i>unit</i> tip
        /// never matches either, or every unit with no tip of its own would answer to any
        /// <c>tip="en"</c> area.
        /// </remarks>
        public static bool IsUnitActivator(string areaTip, string unitTip)
        {
            if (string.IsNullOrEmpty(areaTip) || string.IsNullOrEmpty(unitTip))
            {
                return false;
            }

            if (string.Equals(areaTip, PlayerTip, StringComparison.Ordinal))
            {
                return false;
            }

            return string.Equals(areaTip, unitTip, StringComparison.Ordinal);
        }

        /// <summary>
        /// Where the activator lands, in room-local pixels, y-up.
        /// </summary>
        /// <param name="portTileX"><c>@port</c>'s <c>x</c>, in AS3 columns.</param>
        /// <param name="portTileY"><c>@port</c>'s <c>y</c>, in AS3 rows — <b>0 is the ceiling</b>.</param>
        /// <param name="roomHeightTiles"><c>RoomInstance.height</c>.</param>
        /// <param name="borderOffsetTiles">
        /// <c>RoomInstance.borderOffset</c> — the solid margin the importer adds on both axes, which the
        /// placement flip also subtracts (<c>RoomPopulator.ResolveLegacyPlacementTileCoord</c>).
        /// </param>
        /// <remarks>
        /// <para><c>param1.teleport((portX + 1) * World.tileX, (portY + 1) * World.tileY - 1)</c>
        /// (<c>Area.as:426</c>). Transcribed rather than tidied, including two things that look like
        /// mistakes:</para>
        ///
        /// <list type="bullet">
        /// <item><description><b>The <c>x</c> is a tile <i>boundary</i>, not a centre.</b>
        /// <c>(portX + 1) * tile</c> is the right edge of column <c>portX</c>, so the unit's feet land
        /// centred on the line between two columns. <c>WorldCoordinates.As3BottomCentreAnchorPixels</c>
        /// would put it half a tile further right, and that half tile is a difference from the game.</description></item>
        /// <item><description><b>The <c>-1</c> is a one-pixel lift</b> off that tile's surface, kept
        /// because it decides whether the motor's next step finds the unit standing or one pixel inside
        /// the floor.</description></item>
        /// </list>
        ///
        /// <para>The <c>y</c> is the mirror <c>roomHeight - borderOffset - 1 - portY</c> in tiles, plus
        /// the oracle's <c>+1</c> pixel — the same form as
        /// <c>WorldCoordinates.As3BottomCentreAnchorPixels</c>, which is where the border subtraction was
        /// first worked out. Keeping the two in the same shape is deliberate: they are the same
        /// conversion, and one of them being a tile off is not something a screen check would obviously
        /// show.</para>
        /// </remarks>
        public static void PortDestinationPixels(
            int portTileX, int portTileY, int tileSize, int roomHeightTiles, int borderOffsetTiles,
            out float x, out float y)
        {
            if (tileSize <= 0)
            {
                tileSize = 40;
            }

            if (borderOffsetTiles < 0)
            {
                borderOffsetTiles = 0;
            }

            x = (portTileX + borderOffsetTiles + 1) * tileSize;
            y = (roomHeightTiles - borderOffsetTiles - portTileY - 1) * tileSize + 1f;
        }

        /// <summary>
        /// Half-open overlap between two rectangles given as edges — <c>Obj.areaTest</c>
        /// (<c>Obj.as:287-294</c>): <c>false</c> when <c>a.X1 &gt;= b.X2 || a.X2 &lt;= b.X1 || …</c>.
        /// </summary>
        public static bool Overlaps(
            float aMinX, float aMaxX, float aMinY, float aMaxY,
            float bMinX, float bMaxX, float bMinY, float bMaxY)
        {
            return !(aMinX >= bMaxX || aMaxX <= bMinX || aMinY >= bMaxY || aMaxY <= bMinY);
        }

        /// <summary>
        /// <c>if(active &amp;&amp; !preactive &amp;&amp; onPort)</c> (<c>Area.as:341-344</c>).
        /// </summary>
        public static bool ShouldTeleport(bool wasActive, bool isActive) => isActive && !wasActive;
    }
}
