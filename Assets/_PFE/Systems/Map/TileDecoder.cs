using System;
using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Decodes tile strings from room XML data into TileData objects.
    /// Direct port of AS3 Tile.dec() and Tile.inForm().
    /// 
    /// AS3 tile format: dot-separated strings per row, e.g. "A.Cb._.АА;.CА"
    /// Each tile string: first char = fForm lookup, remaining chars = oForm lookups or modifiers.
    /// 
    /// Special modifier characters (not form lookups):
    ///   * = water
    ///   , = zForm 1 (25% height offset)
    ///   ; = zForm 2 (50% height offset)
    ///   : = zForm 3 (75% height offset)
    ///   _ = empty/air (entire string)
    ///   (empty string) = air
    /// </summary>
    public static class TileDecoder
    {
        /// <summary>
        /// Decode a tile string into a TileData object.
        /// Port of AS3: Tile.dec(param1:String, param2:Boolean = false)
        /// </summary>
        /// <param name="tileString">The tile code (e.g., "C", "Cb", "АА;", "_").</param>
        /// <param name="x">Tile grid X coordinate.</param>
        /// <param name="y">Tile grid Y coordinate.</param>
        /// <param name="formDb">The form database to look up characters.</param>
        /// <param name="mirror">Whether this room is mirrored.</param>
        /// <returns>Populated TileData.</returns>
        public static TileData Decode(string tileString, int x, int y, TileFormDatabase formDb, bool mirror = false)
        {
            TileData tile = new TileData
            {
                gridPosition = new Vector2Int(x, y)
            };

            // Empty or underscore = air
            if (string.IsNullOrEmpty(tileString) || tileString == "_")
            {
                return tile;
            }

            // Reset fields (matches AS3 dec() reset at top)
            // TileData constructor already defaults to air/zero, so this is implicit.

            // --- First character: fForm lookup ---
            // AS3: var _loc3_:int = int(param1.charCodeAt(0));
            //      if(_loc3_ > 64 && _loc3_ != 95) this.inForm(Form.fForms[param1.charAt(0)]);
            char firstChar = tileString[0];
            int charCode = (int)firstChar;

            if (charCode > 64 && charCode != 95) // > '@' and not '_'
            {
                string firstKey = firstChar.ToString();
                TileForm fForm = formDb.GetFForm(firstKey);
                if (fForm != null)
                {
                    ApplyForm(tile, fForm);
                }
                else
                {
                    // Not in fForms — might be a valid character but no form defined
                    // AS3 would get null from the array and inForm would early-return
                    Debug.LogWarning($"[TileDecoder] No fForm for '{firstKey}' at ({x},{y})");
                }
            }

            // --- Remaining characters: oForm lookups or modifiers ---
            // AS3: if(param1.length > 1) { loop from index 1 to end }
            if (tileString.Length > 1)
            {
                for (int i = 1; i < tileString.Length; i++)
                {
                    char c = tileString[i];
                    string key = c.ToString();

                    // Special modifiers (not form lookups)
                    if (c == '*')
                    {
                        tile.hasWater = true;
                    }
                    else if (c == ',')
                    {
                        SetZForm(tile, 1);
                    }
                    else if (c == ';')
                    {
                        SetZForm(tile, 2);
                    }
                    else if (c == ':')
                    {
                        SetZForm(tile, 3);
                    }
                    else
                    {
                        // oForm lookup
                        // AS3 mirror logic: if mirror and form has idMirror, use the mirror form instead
                        TileForm oForm = formDb.GetOForm(key);

                        if (oForm != null && mirror && !string.IsNullOrEmpty(oForm.mirror))
                        {
                            TileForm mirrorForm = formDb.GetOForm(oForm.mirror);
                            if (mirrorForm != null)
                            {
                                oForm = mirrorForm;
                            }
                        }

                        if (oForm != null)
                        {
                            ApplyForm(tile, oForm);
                        }
                        // AS3 silently ignores unknown overlay chars (null check in inForm)
                    }
                }
            }

            // --- Post-processing ---
            // AS3: if(this.zForm == 0) { if(this.zad != "") this.back = this.zad; }
            // zad is stored temporarily; if no height offset, it becomes the back graphic
            if (tile.heightLevel == 0)
            {
                string zad = tile.GetZadGraphic();
                if (!string.IsNullOrEmpty(zad))
                {
                    tile.SetBackGraphic(zad);
                }
            }

            return tile;
        }

        /// <summary>
        /// Apply a form's properties to a tile.
        /// Direct port of AS3: Tile.inForm(param1:Form)
        /// 
        /// Key behaviors:
        /// - ed==2 (tip==2): front goes to tile.back, not tile.front
        /// - vid: first non-zero goes to vid, second goes to vid2
        /// - Properties only overwrite if the form value is non-zero/non-empty
        /// </summary>
        private static void ApplyForm(TileData tile, TileForm form)
        {
            if (form == null) return;

            // --- Visual assignment (depends on ed/tip) ---
            if (form.ed == 2)
            {
                // Background texture form: front graphic goes to tile.back
                // AS3: if(param1.tip == 2) { if(param1.front) this.back = param1.front; }
                if (!string.IsNullOrEmpty(form.front))
                {
                    tile.SetBackGraphic(form.front);
                }
            }
            else
            {
                // Normal form: front graphic goes to tile.front
                if (!string.IsNullOrEmpty(form.front))
                {
                    tile.SetFrontGraphic(form.front);
                    if (form.rear)
                    {
                        tile.frontRear = true;
                    }
                }

                // back goes to zad (temp storage, may become tile.back in post-processing)
                if (!string.IsNullOrEmpty(form.back))
                {
                    tile.SetZadGraphic(form.back);
                }
            }

            // --- Vid (visual variation) ---
            // First non-zero vid goes to vid, second goes to vid2
            if (form.vid > 0)
            {
                if (tile.visualId == 0)
                {
                    tile.visualId = form.vid;
                    if (form.rear)
                    {
                        tile.vidRear = true;
                    }
                }
                else
                {
                    tile.visualId2 = form.vid;
                    if (form.rear)
                    {
                        tile.vid2Rear = true;
                    }
                }
            }

            // --- Properties (only overwrite if form value is non-zero/true) ---
            // AS3: if(param1.mat) this.mat = param1.mat;  (truthy check: 0 is falsy)
            if (form.mat != 0)
                tile.material = (MaterialType)form.mat;

            if (form.hp != 0)
                tile.hitPoints = form.hp;

            if (form.thre != 0)
                tile.damageThreshold = form.thre;

            if (form.indestruct)
                tile.indestructible = true;

            if (form.lurk != 0)
                tile.lurk = form.lurk;

            if (form.phis != 0)
            {
                // Map AS3 phis to Unity TilePhysicsType
                tile.physicsType = MapPhysicsType(form.phis, form.shelf);
            }

            if (form.shelf)
            {
                tile.physicsType = TilePhysicsType.Platform;
                tile.isLedge = true;
            }

            if (form.diagon != 0)
            {
                tile.slopeType = form.diagon;
                // Diagonals with phis=0 are still traversable slopes
                if (tile.physicsType == TilePhysicsType.Air && form.phis == 0)
                {
                    // Slopes from overlays keep current physics (might be air for ramps)
                }
            }

            if (form.stair != 0)
            {
                tile.stairType = form.stair;
                if (tile.physicsType == TilePhysicsType.Air)
                {
                    tile.physicsType = TilePhysicsType.Stair;
                }
            }

            // AS3: if(this.phis > 0) this.opac = 1;
            if (tile.physicsType >= TilePhysicsType.Wall)
            {
                tile.opacity = 1f;
            }
        }

        /// <summary>
        /// Set height level (zForm in AS3).
        /// AS3: Tile.setZForm() adjusts phY1 by zForm/4 of tile height.
        /// </summary>
        private static void SetZForm(TileData tile, int zForm)
        {
            if (zForm < 0) zForm = 0;
            if (zForm > 3) zForm = 3;
            tile.heightLevel = zForm;

            // AS3: if(param1 > 0) this.opac = 0;
            // Raised tiles start transparent (they're partial-height, air on top)
            if (zForm > 0)
            {
                tile.opacity = 0f;
            }
        }

        /// <summary>
        /// Map AS3 phis integer to Unity TilePhysicsType.
        /// </summary>
        private static TilePhysicsType MapPhysicsType(int phis, bool shelf)
        {
            if (shelf) return TilePhysicsType.Platform;

            return phis switch
            {
                0 => TilePhysicsType.Air,
                1 => TilePhysicsType.Wall,
                // AS3 phis=3 is "ghost" wall (temporary, disappears)
                3 => TilePhysicsType.Wall,
                _ => TilePhysicsType.Wall
            };
        }

        /// <summary>
        /// Parse an entire room's tile data from the dot-separated row format.
        /// Each row is a string like "A.Cb._.АА;.CА" where dots separate tile codes.
        /// </summary>
        /// <param name="rows">Array of row strings, one per Y coordinate (top to bottom).</param>
        /// <param name="formDb">Form database for lookups.</param>
        /// <param name="mirror">Whether to mirror the room horizontally.</param>
        /// <param name="roomWidth">Expected room width in tiles.</param>
        /// <param name="roomHeight">Expected room height in tiles.</param>
        /// <returns>2D tile array [x, y].</returns>
        public static TileData[,] ParseRoom(string[] rows, TileFormDatabase formDb, bool mirror, int roomWidth, int roomHeight)
        {
            TileData[,] tiles = new TileData[roomWidth, roomHeight];

            // Initialize all tiles as air
            for (int x = 0; x < roomWidth; x++)
            {
                for (int y = 0; y < roomHeight; y++)
                {
                    tiles[x, y] = new TileData { gridPosition = new Vector2Int(x, y) };
                }
            }

             for (int sourceRow = 0; sourceRow < rows.Length && sourceRow < roomHeight; sourceRow++)
            {
                int y = roomHeight - 1 - sourceRow;
                string row = rows[sourceRow];
                if (string.IsNullOrEmpty(row))
                    continue;

                // AS3: arri = js.split(".");
                // RemoveEmptyEntries handles trailing dots (e.g., "C._E._E.")
                string[] tileCodes = row.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);

                for (int i = 0; i < tileCodes.Length && i < roomWidth; i++)
                {
                    // AS3 mirror: read from opposite end
                    int x = mirror ? (roomWidth - i - 1) : i;
                    string code = tileCodes[i];

                    if (string.IsNullOrEmpty(code))
                        code = "_";

                    tiles[x, y] = Decode(code, x, y, formDb, mirror);
                }
            }

            ApplyLegacyStairPlacementRules(tiles, roomWidth, roomHeight);

            return tiles;
        }

        /// <summary>
        /// Mirrors the original Location.as stair placement pass:
        /// when a stair overlay sits on an air-like tile and the stair direction
        /// changes relative to the tile below, Flash promotes it to a shelf and
        /// advances the tileFront frame by one.
        /// </summary>
        private static void ApplyLegacyStairPlacementRules(TileData[,] tiles, int roomWidth, int roomHeight)
        {
            if (tiles == null)
            {
                return;
            }

            for (int x = 0; x < roomWidth; x++)
            {
                for (int y = 0; y < roomHeight - 1; y++)
                {
                    TileData tile = tiles[x, y];
                    if (!UsesLegacyAirStairPlacement(tile))
                    {
                        continue;
                    }

                    // AS3 room rows are addressed top-down. After ParseRoom remaps them into
                    // Unity's bottom-up coordinates, the original j - 1 neighbor becomes y + 1.
                    TileData tileAbove = tiles[x, y + 1];
                    if (tileAbove == null || tile.stairType == tileAbove.stairType)
                    {
                        continue;
                    }

                    tile.isLedge = true;
                    if (tile.visualId > 0)
                    {
                        tile.visualId++;
                    }
                    else if (tile.visualId2 > 0)
                    {
                        tile.visualId2++;
                    }
                }
            }
        }

        private static bool UsesLegacyAirStairPlacement(TileData tile)
        {
            if (tile == null || tile.stairType == 0 || tile.isLedge)
            {
                return false;
            }

            return tile.physicsType == TilePhysicsType.Air ||
                   tile.physicsType == TilePhysicsType.Stair;
        }
    }
}
