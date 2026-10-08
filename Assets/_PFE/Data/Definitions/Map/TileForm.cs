using System;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Tile form data - port of AS3 Form class.
    /// Defines visual and physical properties for a tile character.
    /// Loaded from tile_forms.json via TileFormDatabase.
    /// 
    /// AS3 reference: fe.loc.Form
    /// Two dictionaries: fForms (primary, first char) and oForms (overlay, subsequent chars).
    /// </summary>
    [Serializable]
    public class TileForm
    {
        /// <summary>Character key used in tile strings (e.g., "A", "Б", "-").</summary>
        public string id;

        /// <summary>
        /// Form type from AS3 @ed attribute:
        ///   1 = fForm (primary wall/solid)
        ///   2 = background texture (tip==2: front goes to tile.back, not tile.front)
        ///   3 = stair overlay
        ///   4 = shelf/diagonal overlay
        ///   0 = special (e.g., "100")
        /// </summary>
        public int ed;

        // --- Visual ---

        /// <summary>
        /// Front graphic identifier (MovieClip frame label in AS3).
        /// Empty when vid > 0 (vid takes precedence).
        /// When ed==2, this gets assigned to tile.back instead of tile.front.
        /// </summary>
        public string front = "";

        /// <summary>
        /// Back/behind graphic identifier. Goes to tile.zad in AS3,
        /// which becomes tile.back when zForm == 0.
        /// </summary>
        public string back = "";

        /// <summary>Visual variation ID. When > 0, overrides front graphic lookup.</summary>
        public int vid;

        /// <summary>Render behind player/units (rear layer).</summary>
        public bool rear;

        // --- Physics ---

        /// <summary>
        /// Physics type: 0 = air; 1 = solid; 2 = a level-2 solid (the grate doors,
        /// <c>AllData.as:4859-4860</c>); 3 = a ghost wall, set only at runtime (<c>Spell.as:446</c>).
        /// Non-zero is what makes a tile opaque (<c>Tile.as:198-201</c>).
        /// </summary>
        public int phis;

        /// <summary>
        /// One-way platform flag (AS3 <c>shelf</c>). <b>A flag — it is NOT a physics assignment.</b>
        /// AS3 assigns it independently and never lets it touch <c>phis</c>
        /// (<c>Tile.as:182-189</c>), and its only consumer reads it when <c>phis == 0 || phis == 3</c>
        /// (<c>Box.as:1262, 1270</c>) — so on a wall the flag is inert. It decodes to
        /// <see cref="TilePhysicsType.Platform"/> only where that would not overwrite a wall.
        /// </summary>
        public bool shelf;

        /// <summary>Diagonal/slope direction: -1=left, 0=none, 1=right.</summary>
        public int diagon;

        /// <summary>Stair direction: -1=descending, 0=none, 1=ascending.</summary>
        public int stair;

        // --- Durability ---

        /// <summary>Material type: 0=default, 1=concrete, 2=brick, 3=wood, 4=metal, 6=organic.</summary>
        public int mat;

        /// <summary>Hit points.</summary>
        public int hp;

        /// <summary>Damage threshold (hits below this do nothing).</summary>
        public int thre;

        /// <summary>Cannot be destroyed.</summary>
        public bool indestruct;

        // --- Special ---

        /// <summary>Lurk/stealth value (allows hiding).</summary>
        public int lurk;

        /// <summary>Mirror form ID (for mirrored rooms).</summary>
        public string mirror = "";
    }
}