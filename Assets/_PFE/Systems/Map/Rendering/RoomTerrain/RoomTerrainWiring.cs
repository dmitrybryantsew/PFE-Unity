using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Systems.Map.Rendering.RoomTerrain
{
    /// <summary>
    /// The decision layer between the game and <see cref="RoomTerrainRenderer"/>: whether to build the
    /// quarter renderer for a room, and with which numbers.
    ///
    /// <para><b>Why this is a separate, Unity-free-ish static class rather than three <c>if</c>s inside
    /// <see cref="RoomVisualController"/>.</b> The dangerous case is not "the renderer draws the wrong
    /// picture" — it is "the renderer is not built at all, and nothing says so". A room with the flag on
    /// and the art asset missing draws <i>no terrain whatsoever</i>: the shipped tiles are suppressed and
    /// the replacement never ran, which reads as "the room failed to load". That is a decision with three
    /// inputs and a reason, so it is a function that returns the reason by name, and a fixture that asks
    /// it each of the four ways.</para>
    ///
    /// <para><b>Nothing here reads <see cref="PfeDebugSettings"/>.</b> The caller passes the flag in.
    /// The settings object is a <c>Resources.Load</c> and cannot be constructed in an offline test, so a
    /// resolver that read it directly would be untestable — and the one thing this class exists for is
    /// being testable.</para>
    /// </summary>
    public static class RoomTerrainWiring
    {
        /// <summary>
        /// Where the built art asset lives. The importer writes it here
        /// (<c>Editor/RoomTerrain/RoomTerrainArtImporter.cs</c>) and the A/B window loads it from here.
        ///
        /// <para><b>Editor-only.</b> It is not under a <c>Resources</c> folder, so a Player build can only
        /// get it through the serialized field on <see cref="RoomVisualController"/>. This constant is
        /// what makes the path work in the editor with zero scene edits — the owner can flip the console
        /// verb and see the picture without touching the scene first.</para>
        /// </summary>
        public const string EditorArtAssetPath = "Assets/_PFE/Data/RoomTerrainArt.asset";

        /// <summary>Resources path, used when a serialized reference is absent. Absent today.</summary>
        public const string ResourcesArtPath = "RoomTerrainArt";

        /// <summary>
        /// One room cell in world units, from the <b>same constant the shipped renderer uses</b>.
        ///
        /// <para><see cref="TileRenderer.ApplyScale"/> computes <c>WorldConstants.TILE_SIZE / 100f</c>,
        /// and <see cref="RoomTerrainGeometry.DefaultCellSize"/> states the same 0.4. Taking the value
        /// from <see cref="WorldConstants"/> here rather than from the literal means a change to the tile
        /// size moves this renderer too; the two are pinned together by
        /// <c>RoomTerrainWiringTests.CellSize_IsTheShippedTileSize</c>, because a 0.4 that silently
        /// disagreed with the shipped path would draw a room at the wrong scale next to units that use
        /// the other one.</para>
        /// </summary>
        public static float ShippedCellSize => WorldConstants.TILE_SIZE / 100f;

        /// <summary>
        /// Whether the quarter renderer should be built for this room, and — when the answer is no — why
        /// not, in a sentence the log line can print verbatim.
        /// </summary>
        /// <param name="enabled">The debug flag, read once per room build.</param>
        /// <param name="art">The built art asset, or null when the importer has never run.</param>
        /// <param name="materials">The material database the controller already holds.</param>
        /// <param name="reason">Null on success; otherwise what is missing.</param>
        public static bool TryResolve(
            bool enabled,
            RoomTerrainArt art,
            MaterialRenderDatabase materials,
            out string reason)
        {
            if (!enabled)
            {
                reason = "the quad-terrain flag is off";
                return false;
            }

            if (art == null)
            {
                reason = "the room terrain art asset is missing (run PFE/Art/Rebuild Room Terrain Art)";
                return false;
            }

            if (materials == null)
            {
                reason = "no MaterialRenderDatabase is assigned on this RoomVisualController";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// The renderer options for the game, as opposed to the A/B window's.
        ///
        /// <para><b>Everything is the renderer's own default, and that is the point.</b> The one number
        /// that is <i>not</i> left at its default elsewhere is <c>ambient</c>, and it must stay
        /// <b>1</b>: the room's darkness is already folded into <see cref="RoomTerrainRenderer.BackTint"/>
        /// — <see cref="RoomBackdropRenderer.ResolveBackgroundTileTint"/> multiplies by
        /// <c>ResolveTileDarknessMultiplier(environment.darkness)</c> — so passing the darkness again as
        /// <c>_Ambient</c> would darken a dark room twice. Pinned by
        /// <c>RoomTerrainWiringTests.Options_DoNotApplyTheDarknessTwice</c>.</para>
        ///
        /// <para><b>Front tint stays on</b> (AS3's <c>Location.colorFilter(loc.color)</c> over the whole
        /// front bitmap, <c>Grafon.as:617</c>), which is the one place this renderer deliberately differs
        /// from the shipped path. Turning it off in game would make the two paths agree in colour by
        /// making the new one wrong.</para>
        /// </summary>
        public static RoomTerrainRenderer.Options ResolveOptions(float cellSize)
        {
            RoomTerrainRenderer.Options options = RoomTerrainRenderer.Options.Default();

            if (cellSize > 0f)
            {
                options.cellSize = cellSize;
            }

            return options;
        }

        /// <summary>
        /// The art asset, from the serialized reference if the scene has one and from the editor's asset
        /// database otherwise.
        ///
        /// <para><b>Deliberately not <c>Resources.Load</c> first.</b> The asset is not under a
        /// <c>Resources</c> folder — it is 81 sprite references and 5 textures, and duplicating it into
        /// one would be a second copy that goes stale the moment the importer runs again. The
        /// <c>Resources</c> path is tried second so that moving the asset is a one-line change rather
        /// than a code edit, and the editor path last because it is the only one that works in a build
        /// and therefore must not mask a missing serialized reference in the editor.</para>
        /// </summary>
        public static RoomTerrainArt ResolveArt(RoomTerrainArt serialized)
        {
            if (serialized != null) return serialized;

            RoomTerrainArt fromResources = Resources.Load<RoomTerrainArt>(ResourcesArtPath);
            if (fromResources != null) return fromResources;

#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<RoomTerrainArt>(EditorArtAssetPath);
#else
            return null;
#endif
        }
    }
}
