using System;
using UnityEngine;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.Rendering.RoomTerrain;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// The room terrain renderer's status, exposed to Lua as the global <c>terrain</c> and to the
    /// REPL as <c>terrain</c> / <c>terrain status</c>.
    ///
    /// <para><b>Status only — there is nothing to switch anymore.</b> The quarter renderer
    /// (<see cref="RoomTerrainRenderer"/>) is the only terrain renderer since the shipped per-tile
    /// path was retired, so the old <c>terrain quad on|off</c> verb went with it. What can still go
    /// wrong is an input to the one renderer that remains: the art asset missing, or the material
    /// database absent. <see cref="Status"/> resolves the art asset up front and names it, then asks
    /// the live controller what it built — the question <see cref="RoomTerrainWiring.TryResolve"/>
    /// asks at room build, asked here so you can find out before the transition instead of after.</para>
    ///
    /// <para><b>MoonSharp visibility.</b> Only the command methods are public; the wiring is private, as
    /// with the other command objects, because MoonSharp's default reflection interop exposes public
    /// members only.</para>
    /// </summary>
    [LocalOnly]
    public sealed class DevConsoleTerrainCommands
    {
        /// <summary>Production constructor: nothing to resolve — the settings asset no longer carries a terrain flag.</summary>
        public DevConsoleTerrainCommands()
        {
        }

        /// <summary>
        /// Whether the art the renderer depends on resolves, and which renderer the room you are
        /// standing in actually got.
        /// </summary>
        public string Status()
        {
            RoomTerrainArt art = RoomTerrainWiring.ResolveArt(null);

            return $"[terrain] {ArtLine(art)} · cell {RoomTerrainWiring.ShippedCellSize:0.###} · {CurrentRoomLine()}";
        }

        /// <summary>
        /// Where the art stands, told honestly in both contexts.
        ///
        /// <para><b>The editor and a build get it from different places.</b> In the editor the asset is
        /// reachable by path, so a null here is a real "the importer has not run". In a build there is no
        /// asset database: the art arrives through the serialized reference on
        /// <see cref="RoomVisualController"/>, which this command cannot see — so claiming it is missing
        /// would be a false alarm in exactly the build where the reference was deliberately assigned.</para>
        /// </summary>
        private static string ArtLine(RoomTerrainArt art)
        {
#if UNITY_EDITOR
            return art != null
                ? "art: found"
                : "art: MISSING — run PFE/Art/Rebuild Room Terrain Art";
#else
            return "art: from the scene reference (not reachable from the console in a build)";
#endif
        }

        /// <summary>
        /// Which renderer the room currently built actually uses.
        ///
        /// <para>This is the one line that distinguishes "the art resolved" from "the room in front of
        /// you was drawn by it" — the two are different facts, and the second is the one being asked
        /// about. A scene search per call is fine: this is a debug verb, not a per-frame path.</para>
        /// </summary>
        private static string CurrentRoomLine()
        {
            RoomVisualController controller = UnityEngine.Object.FindFirstObjectByType<RoomVisualController>();
            if (controller == null) return "this room: no controller";

            RoomTerrainRenderer renderer = controller.QuadTerrainRenderer;
            return renderer == null
                ? "this room: NOT BUILT — the quarter renderer is the only terrain renderer; a missing line here means the build failed, check the log"
                : $"this room: quarter renderer ({renderer.ShapesPlaced} shape(s), {renderer.LayerCount} layer(s))";
        }

        /// <summary>Usage summary, so the console's <c>help</c> has a single source for it.</summary>
        public string Help()
        {
            return "terrain             - status: art asset resolution and which renderer the current room got\n" +
                   "terrain status      - same as `terrain`\n" +
                   "Lua: terrain:Status()";
        }
    }
}
