using System;
using UnityEngine;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.Rendering.RoomTerrain;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// The room terrain renderer switch, exposed to Lua as the global <c>terrain</c>:
    /// <c>terrain:SetQuad(true)</c>, and to the REPL as <c>terrain quad on|off</c>.
    ///
    /// <para><b>Why a verb and not just the Inspector field.</b> The flag lives on
    /// <see cref="PfeDebugSettings"/>, which is an asset — so without this verb, trying the quarter
    /// renderer means editing a project asset, and every experiment leaves a diff behind. The verb
    /// writes the same field the Inspector edits, so the two cannot disagree, and a session that ends
    /// with the flag on leaves no trace in the repository.</para>
    ///
    /// <para><b>The status line answers the question that a picture cannot.</b> With the flag on and the
    /// art asset missing, the room falls back to the shipped renderer rather than drawing nothing — so the
    /// question is not "is the room empty" but "which renderer did this room actually get", and that is a
    /// fact only the console can report without a screenshot. <see cref="Status"/> resolves the art asset
    /// up front and names it, then asks the live controller what it built. It is the same question
    /// <see cref="RoomTerrainWiring.TryResolve"/> asks at room build; asking it here is how you find out
    /// before the transition instead of after.</para>
    ///
    /// <para><b>MoonSharp visibility.</b> Only the command methods are public; the wiring is private, as
    /// with the other command objects, because MoonSharp's default reflection interop exposes public
    /// members only.</para>
    /// </summary>
    [LocalOnly]
    public sealed class DevConsoleTerrainCommands
    {
        private readonly Func<PfeDebugSettings> _settingsSource;

        /// <summary>Production constructor: read the live settings asset, as every other command object does.</summary>
        public DevConsoleTerrainCommands() : this(null)
        {
        }

        /// <summary>
        /// Test seam for the settings source — the same one <see cref="DevConsoleUiCommands"/> carries and
        /// for the same reason: <c>DebugOverlays.Settings</c> is a <c>Resources.Load</c>, and an offline
        /// test cannot execute an ECall, so a command object reachable only through the live asset could
        /// not be tested at all. A null source falls back to the live asset, so production code cannot
        /// accidentally get a command object that reports "no settings" because of a wiring slip.
        /// </summary>
        public DevConsoleTerrainCommands(Func<PfeDebugSettings> settingsSource)
        {
            _settingsSource = settingsSource ?? (() => DebugOverlays.Settings);
        }

        /// <summary>
        /// The flag, whether the art the flag depends on resolves, which way the next room will be drawn,
        /// and which renderer the room you are standing in actually got.
        /// </summary>
        public string Status()
        {
            PfeDebugSettings settings = _settingsSource();
            if (settings == null) return NoSettings;

            bool on = settings.QuadRoomTerrain;
            RoomTerrainArt art = RoomTerrainWiring.ResolveArt(null);

            // The verdict names the *next* room, not this one: the flag is read once per build, so a
            // room you are standing in keeps whatever it was built with. Saying "on" without that
            // qualifier is how a toggle gets reported as broken.
            string verdict = on && art != null
                ? "next room: QUARTER"
                : on
                    ? "next room: shipped (no art)"
                    : "next room: shipped";

            return $"[terrain] quad: {(on ? "on" : "off")} · {ArtLine(art)} · cell {RoomTerrainWiring.ShippedCellSize:0.###} · {verdict} · {CurrentRoomLine()}";
        }

        /// <summary>
        /// Where the art stands, told honestly in both contexts.
        ///
        /// <para><b>The editor and a build get it from different places.</b> In the editor the asset is
        /// reachable by path, so a null here is a real "the importer has not run". In a build there is no
        /// asset database: the art arrives through the serialized reference on
        /// <see cref="RoomVisualController"/>, which this command cannot see — so claiming it is missing
        /// would be a false alarm in exactly the build where the flag was deliberately enabled.</para>
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
        /// <para>This is the one line that distinguishes "the flag is on" from "the room in front of you
        /// is drawn by it" — the two are different facts, and the second is the one being asked about.
        /// A scene search per call is fine: this is a debug verb, not a per-frame path.</para>
        /// </summary>
        private static string CurrentRoomLine()
        {
            RoomVisualController controller = UnityEngine.Object.FindFirstObjectByType<RoomVisualController>();
            if (controller == null) return "this room: no controller";

            RoomTerrainRenderer renderer = controller.QuadTerrainRenderer;
            return renderer == null
                ? "this room: shipped"
                : $"this room: QUARTER ({renderer.ShapesPlaced} shape(s), {renderer.LayerCount} layer(s))";
        }

        /// <summary>
        /// Turn the quarter renderer on or off for the next room that is built.
        ///
        /// <para>Does not rebuild the current room: a room's terrain is built once, at
        /// <c>RoomVisualController.Initialize</c>, and re-entering is what re-reads the flag. The reply
        /// says so rather than leaving the caller to wonder why the picture did not change.</para>
        /// </summary>
        public string SetQuad(bool on)
        {
            PfeDebugSettings settings = _settingsSource();
            if (settings == null) return NoSettings;

            settings.QuadRoomTerrain = on;
            return Status() + "\n  (takes effect when the next room is built — walk through a door)";
        }

        /// <summary>Usage summary, so the console's <c>help</c> has a single source for it.</summary>
        public string Help()
        {
            return "terrain             - status: quad flag, art asset, and which way the next room is drawn\n" +
                   "terrain quad        - same as `terrain`\n" +
                   "terrain quad on     - draw the next room with the quarter renderer (the default)\n" +
                   "terrain quad off    - fall back to the shipped per-tile compositor, sprites and all\n" +
                   "Lua: terrain:SetQuad(true | false)";
        }

        private const string NoSettings = "[terrain] PfeDebugSettings not found in Resources.";
    }
}
