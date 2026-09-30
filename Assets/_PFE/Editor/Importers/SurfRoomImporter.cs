using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Disabled. Kept as a signpost because the reason it existed is a bug that has since been fixed
    /// elsewhere, and re-arming it would now do damage.
    ///
    /// This importer extracted the <c>rooms_surf</c> XML field from <c>Rooms.as</c> and wrote it to
    /// <c>Resources/Rooms/Surf</c> under the collection id <c>"Surf"</c>, then deleted thirteen rooms it
    /// considered "misplaced" from <c>Resources/Rooms/Base</c>. Both halves were workarounds for one
    /// defect: <c>RoomTemplateImporterWindow</c> named the output folder after the <i>source file</i>,
    /// while the parser named the collection after the <i>XML field</i>. Because <c>Rooms.as</c> declares
    /// four lands (<c>rooms_begin</c>, <c>rooms_surf</c>, <c>rooms_garages</c>, <c>rooms_way</c>), all
    /// four were folded into one folder named "Base" and the later lands silently overwrote the earlier
    /// ones — which is why surf rooms had to be fished back out and "misplaced" rooms deleted.
    ///
    /// That is fixed: the importer now folders by collection id, so <c>rooms_surf</c> lands in
    /// <c>Resources/Rooms/rooms_surf</c> with collection id <c>rooms_surf</c> — the id the oracle uses
    /// (<c>&lt;land id='surf' file='rooms_surf'&gt;</c> in GameData.as, and <c>this.rooms["rooms_surf"]</c>
    /// in Rooms.as:2795). Running this importer again would write a second, duplicate collection named
    /// "Surf" for the same land, under an id nothing in the oracle or the land-defaults database
    /// recognises.
    ///
    /// It also no longer runs itself. It used to be wired to <c>[InitializeOnLoadMethod]</c>, so every
    /// editor load re-imported and re-deleted without being asked — a destructive action with no
    /// confirmation and no undo.
    /// </summary>
    public static class SurfRoomImporter
    {
        [MenuItem("PFE/Map/Import Surf (Wasteland) Rooms", false, 25)]
        public static void ImportSurfRooms()
        {
            Debug.LogWarning(
                "[SurfRoomImporter] Disabled. Surf rooms are imported by 'PFE/Map/Room Template Importer' " +
                "into Resources/Rooms/rooms_surf with collection id 'rooms_surf'. This importer would write " +
                "a duplicate collection named 'Surf' for the same land, under an id the oracle and the " +
                "land-defaults database do not use. See the class comment for the history.");
        }
    }
}
