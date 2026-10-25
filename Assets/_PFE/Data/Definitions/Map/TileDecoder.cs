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
                // Map AS3 phis to Unity TilePhysicsType.
                // AS3 `Tile.inForm()` reads only `param1.phis` here (Tile.as:182-185); `shelf` is an
                // independent flag assigned further down, so it must not colour this mapping.
                tile.physicsType = MapPhysicsType(form.phis);
            }

            // AS3 `if(param1.shelf) this.shelf = true;` (Tile.as:186-189) — `shelf` is a FLAG; it
            // never touches `phis`. The only place the flag is read for collision is
            // `Box.as:1262,1270`, and BOTH arms there are gated on `phis == 0 || phis == 3`: on a
            // `phis == 1` wall the flag is inert.
            //
            // The port used to write `physicsType = Platform` unconditionally, so a beam overlay on a
            // wall DEMOTED the wall — it stopped blocking light and stopped blocking the sight ray.
            // Guarding on `!= Wall` keeps the flag's real scope (a shelf over open air, or over a
            // ladder, is still a one-way platform) without letting it erase a wall.
            if (form.shelf)
            {
                if (tile.physicsType != TilePhysicsType.Wall)
                {
                    tile.physicsType = TilePhysicsType.Platform;
                }
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
        /// Map AS3 <c>phis</c> to the port's <see cref="TilePhysicsType"/>.
        ///
        /// <para><c>phis</c> alone decides. 0 is air; 1 (solid), 2 (a level-2 solid — the grate doors
        /// at <c>AllData.as:4859-4860</c>) and 3 (a runtime ghost wall, <c>Spell.as:446</c>) are all
        /// walls. There is deliberately no <c>shelf</c> parameter: AS3 never lets the shelf flag
        /// change <c>phis</c> (<c>Tile.as:182-189</c>), and passing one in was how a shelf overlay
        /// came to overwrite a wall.</para>
        /// </summary>
        private static TilePhysicsType MapPhysicsType(int phis)
        {
            return phis switch
            {
                0 => TilePhysicsType.Air,
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
                // AS3 keeps empty entries and then indexes a FIXED 0..spaceX-1 range,
                // so an authored "A..C" leaves column 1 empty and keeps C at column 2.
                // RemoveEmptyEntries instead collapses the gap and shifts every later
                // token left by one, which corrupts the row and leaves air at the end.
                // That was latent while every authored row was dense, but it is reachable
                // the moment a room editor writes an empty cell.
                string[] tileCodes = row.Split(new[] { '.' });

                for (int i = 0; i < roomWidth; i++)
                {
                    // AS3 mirror: read from opposite end
                    int x = mirror ? (roomWidth - i - 1) : i;
                    string code = i < tileCodes.Length ? tileCodes[i] : string.Empty;

                    if (string.IsNullOrEmpty(code))
                        code = "_";

                    tiles[x, y] = Decode(code, x, y, formDb, mirror);
                }
            }

            ApplyLegacyStairPlacementRules(tiles, roomWidth, roomHeight);

            return tiles;
        }

        /// <summary>
        /// Mirrors the original Location.as stair placement pass: when a stair overlay sits on an
        /// air-like tile and the stair direction changes relative to the tile <b>above</b>, Flash
        /// promotes it to a shelf and advances the tileFront frame by one.
        ///
        /// <para><b>What the promoted frame actually is.</b> The only two forms with <c>stair != 0</c>
        /// are <c>А</c> ("Лестница справа", <c>vid='3' stair='1'</c>) and <c>Б</c> ("Лестница слева",
        /// <c>vid='1' stair='-1'</c>) — <b>ladders</b>, not stairs. Their next frames (4 and 2) are the
        /// ladder's <i>upper half</i>: the exported art for frames 2 and 4 starts at canvas
        /// <c>y = 0</c>, i.e. 30 px <i>above</i> the registration point, while frames 1 and 3 start at
        /// <c>y = 30</c>. So the promotion is what makes a ladder poke up through the opening in the
        /// floor above, and it fires on the <i>topmost</i> cell of a ladder column — the cell whose
        /// above-neighbour is air. It is not a stair-ledge rule, and the 12 tread forms
        /// (<c>В Г Ж З И Й Л М О П С Т</c>) all carry <c>stair = 0</c>, so they can never be promoted.</para>
        ///
        /// <para><b>AS3's <c>!shelf</c> guard is not reproduced, and cannot matter.</b> The oracle tests
        /// <c>!this.space[i][j].shelf</c>, the <i>form's</i> shelf flag, where this uses
        /// <see cref="TileData.isLedge"/>. The two differ only for a form that is both a shelf and a
        /// stair; in <c>AllData.as</c> the six shelf forms (<c>- Д Е К Н Р</c>) are the beams and all
        /// have <c>stair = 0</c> and <c>vid = 0</c>, so the oracle's guard is unreachable. Recorded
        /// because "we substituted our own flag for the oracle's" reads like a defect and is not
        /// one.</para>
        ///
        /// <para><b>The promotion is a COLLISION change, not just a frame change.</b> AS3 sets
        /// <c>shelf = true</c> (<c>Location.as:582</c>), and <c>shelf</c> is the one-way platform flag:
        /// <c>collisionTile</c> answers 1 for it whenever the unit's feet are at or below its top
        /// (<c>Unit.as:2578</c>). So the topmost ladder cell is what a climber stands on when the
        /// climb runs out — that is how a ladder is exited upward at all. The port copied the
        /// <c>vid</c> bump (the art) and dropped the flag, so the cap drew correctly and had no
        /// floor: the climb ended in mid-air and the unit either fell back down the shaft or — with
        /// the old body-wide ladder probe — hovered above the rungs. <see cref="TilePhysicsType.Platform"/>
        /// is this port's <c>shelf</c> (see <c>SurfaceKindRule</c>), and <c>stairType</c> survives the
        /// change, so the cell stays climbable: <c>IsClimbableLadder()</c> reads <c>stairType</c> and
        /// <c>slopeType</c>, never <c>physicsType</c>.</para>
        ///
        /// <para>The <see cref="UsesLegacyAirStairPlacement"/> gate is what keeps this safe — it admits
        /// only <see cref="TilePhysicsType.Air"/> and <see cref="TilePhysicsType.Stair"/>, so the
        /// promotion can never demote a wall, which is the mistake the <c>form.shelf</c> branch in
        /// <c>ApplyForm</c> already carries a guard against.</para>
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

                    // AS3 `this.space[i][j].shelf = true` — the cap is a one-way platform.
                    tile.physicsType = TilePhysicsType.Platform;

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
