namespace PFE.Systems.Map
{
    /// <summary>How a decal is composited. AS3 <c>BitmapData.draw(..., blendMode)</c>.</summary>
    public enum TileDecalBlend
    {
        /// <summary><c>"normal"</c> — the default for every bullet hole, crack and chunk scar.</summary>
        Normal = 0,

        /// <summary>
        /// <c>"hardlight"</c> — every energy decal (plasma, laser, cryo) and <b>every</b> death decal.
        /// Hardlight is not additive: it keeps the destination's darks and pushes highlights, which is
        /// why a plasma burn reads as burned-in rather than as a glow laid on top.
        /// </summary>
        Hardlight = 1,
    }

    /// <summary>
    /// Which of the four orthogonal neighbours of a destroyed tile still hold a solid tile.
    ///
    /// <para><b>Why the death decal needs this at all.</b> A tile-death scorch is 71x72 against a
    /// 40x40 cell, so it covers its neighbours as much as its own cell. AS3 clips it: <c>decal()</c>
    /// saves the destination alpha of the scorch's rect, draws, then restores it
    /// (<c>Grafon.as:1301-1330</c>), so the rubble only appears where the wall still has alpha — the
    /// rim of the hole, never inside it. The port's equivalent of "the wall has alpha here" is "this
    /// neighbour still has a solid tile", so the scorch is stamped as one cell-sized patch per solid
    /// neighbour instead of as a single 1.78-tile sprite.</para>
    /// </summary>
    [System.Flags]
    public enum TileNeighbourSides
    {
        None = 0,

        /// <summary>Grid <c>y + 1</c>. <c>TileData.GetBounds</c> maps grid y straight onto world +Y.</summary>
        North = 1,

        /// <summary>Grid <c>y - 1</c>.</summary>
        South = 2,

        /// <summary>Grid <c>x + 1</c>.</summary>
        East = 4,

        /// <summary>Grid <c>x - 1</c>.</summary>
        West = 8,

        All = North | South | East | West,
    }

    /// <summary>
    /// One <c>Grafon.decal()</c> call, resolved: the two sprites, the transform, the blend, and the
    /// debris emission that goes with it.
    ///
    /// <para><b>Two sprites, and they are not interchangeable.</b> AS3 passes
    /// <c>decal(eraseClass, scorchClass, x, y, scale, rotation, blend)</c>. The first is drawn with
    /// <c>BlendMode.ERASE</c> into <c>frontBmp</c> — it punches alpha out of the wall so the backdrop
    /// shows through, which is what makes a bullet hole read as a hole and not as a dark dot. The second
    /// is drawn on top of the freshly erased pixels as the scorch. <b>Erase is not a tint</b>: dropping
    /// it leaves a decal that looks pasted on, and the two sprites have different sizes because they are
    /// two different shapes (a 5×5 <c>bullet_dyr</c> under a 24×24 <c>bullet_tre</c>).</para>
    ///
    /// <para><b>Every field is nullable on purpose.</b> Plenty of arms in the oracle produce only one of
    /// the two — a metal wall takes <c>bullet_metal</c> and no erase at all, a wood plank takes both, a
    /// plasma hit on an indestructible surface takes <c>plasma_soft</c> with no hole. A port that always
    /// emits both would be wrong in a way that is invisible until someone shoots a pipe.</para>
    /// </summary>
    public struct TileDecalResult
    {
        /// <summary>Sprite name for the ERASE pass, or <c>null</c> when this arm has no hole.</summary>
        public string EraseSprite;

        /// <summary>Sprite name for the scorch pass, or <c>null</c> when this arm has no mark.</summary>
        public string ScorchSprite;

        /// <summary>Uniform scale applied to both sprites. <c>1</c> is the oracle's default.</summary>
        public float Scale;

        /// <summary>
        /// Rotation in <b>radians</b>. AS3 builds <c>_loc12_ = random * 360</c> and hands it to
        /// <c>Matrix.rotate()</c>, which takes radians — so the oracle's "0..360" is 57 full turns, and
        /// the visual result is simply a uniform random angle. Kept in radians rather than "corrected"
        /// to degrees so a reader can check it against the source; the caller passes it straight to
        /// <c>Quaternion.Euler(0, 0, rotation * Mathf.Rad2Deg)</c>.
        /// </summary>
        public float RotationRadians;

        /// <summary>Blend mode for the scorch pass. The erase pass is always erase.</summary>
        public TileDecalBlend Blend;

        /// <summary>Particle definition id for the debris puff, or <c>null</c>.</summary>
        public string ParticleId;

        /// <summary>How many particles <see cref="ParticleId"/> emits. <c>kol</c> in the oracle.</summary>
        public int ParticleCount;

        /// <summary>
        /// Second particle emission, used only by the pole arm of <c>tileDie</c> (<c>fake</c> plus
        /// <c>pole</c>). <c>null</c> everywhere else.
        /// </summary>
        public string ExtraParticleId;

        /// <summary>How many of <see cref="ExtraParticleId"/> to emit.</summary>
        public int ExtraParticleCount;

        /// <summary>True when this call draws nothing and emits nothing — a genuine no-op.</summary>
        public bool IsEmpty =>
            EraseSprite == null && ScorchSprite == null && ParticleId == null && ExtraParticleId == null;
    }

    /// <summary>
    /// AS3 <c>Grafon.dyrka()</c> (<c>Grafon.as:1055-1259</c>) and <c>Grafon.tileDie()</c>
    /// (<c>Grafon.as:968-1053</c>) as pure selection: <b>given the decal type, the material, whether the
    /// tile refused the hit, and how much of its hp the hit was worth, which sprites appear?</b>
    ///
    /// <para><b>This is the whole of "tiles take visible damage".</b> The oracle has no per-tile damage
    /// texture, no crack level and no hp-driven sprite swap — <c>Grafon.as</c> never reads
    /// <c>Tile.hp</c> (grep it: zero hits). A wall that has been shot twenty times looks battered purely
    /// because twenty decals have been stamped into the room bitmap, and the only thing <c>hp</c> decides
    /// is whether the tile is gone. So this table <i>is</i> the feature; there is nothing else to port.</para>
    ///
    /// <para><b>Two tables, keyed differently, and they are not alternatives.</b> <c>dyrka</c> is keyed
    /// on material first and decal type second (a metal wall and a concrete wall answer type 9
    /// differently), then the type table at <c>:1183-1258</c> runs <i>after</i> it and assigns the same
    /// two sprite slots for types 11..19. Both orders are load-bearing and are reproduced here in the
    /// oracle's sequence, not as a switch on type. What survives the second table is the <b>debris</b>:
    /// a plasma hit on a pipe still throws its five <c>pole</c> shards, because the type arm never
    /// touches the particle.</para>
    ///
    /// <para><b>Randomness is injected, not consumed.</b> The oracle calls <c>Math.random()</c> up to
    /// three times per hit — scale, rotation, and one gate (<c>rand &gt; 0.5</c> for a concrete hole,
    /// <c>rand &lt; 0.1</c> for a fire mark, <c>rand * 0.5 &lt; ratio</c> for a scorch). The caller passes
    /// the rolls, so a fixture can pin "the same hit draws the same decal" without seeding a global.
    /// <b>At most one gate roll is consumed per call</b> — the arms are exclusive — which is why one
    /// <c>gateRoll</c> parameter is enough.</para>
    /// </summary>
    public static class TileDecalRules
    {
        // ── Sprite names, exactly the SWF class names ────────────────────────────────────────────
        // Spelled out as constants rather than literals so a typo is a compile error and so the
        // importer and the runtime cannot disagree about the name of a file.
        public const string BulletMetal = "bullet_metal";
        public const string BulletTre = "bullet_tre";
        public const string BulletDyr = "bullet_dyr";
        public const string BulletWood = "bullet_wood";
        public const string PunchTre = "punch_tre";
        public const string ExplTre = "expl_tre";
        public const string MetalTre = "metal_tre";
        public const string FireSoft = "fire_soft";
        public const string LaserTre = "laser_tre";
        public const string PlasmaTre = "plasma_tre";
        public const string PlasmaDyr = "plasma_dyr";
        public const string PlasmaSoft = "plasma_soft";
        public const string BluPlasmaTre = "bluplasma_tre";
        public const string PinkPlasmaTre = "pinkplasma_tre";
        public const string CryoSoft = "cryo_soft";
        public const string PlaExplTre = "plaexpl_tre";
        public const string BlockDyr = "block_dyr";
        public const string BlockTre = "block_tre";
        public const string BlockBur = "block_bur";
        public const string BlockPlav = "block_plav";
        public const string BlockPla = "block_pla";
        public const string TileMask = "TileMask";

        // ── Death-scorch patches ─────────────────────────────────────────────────────────────────
        // A death scorch is baked once per neighbour cell by `.workbuddy-ai/tools/bake_tile_decal_patches.py`
        // and lands in Resources/TileDecals as `<scorch>_n`, `_s`, `_e`, `_w`. See TileNeighbourSides.
        public const string PatchNorth = "_n";
        public const string PatchSouth = "_s";
        public const string PatchEast = "_e";
        public const string PatchWest = "_w";

        /// <summary>
        /// The resource name of the piece of <paramref name="scorchSprite"/> that falls in the
        /// neighbour cell on <paramref name="side"/>, or <c>null</c> when there is nothing to draw.
        /// </summary>
        /// <param name="scorchSprite">A death scorch — <c>block_tre</c>, <c>block_bur</c>, <c>block_pla</c>.</param>
        /// <param name="side">Exactly one side. <see cref="TileNeighbourSides.All"/> is not a patch.</param>
        public static string PatchSprite(string scorchSprite, TileNeighbourSides side)
        {
            if (string.IsNullOrEmpty(scorchSprite)) return null;

            switch (side)
            {
                case TileNeighbourSides.North: return scorchSprite + PatchNorth;
                case TileNeighbourSides.South: return scorchSprite + PatchSouth;
                case TileNeighbourSides.East: return scorchSprite + PatchEast;
                case TileNeighbourSides.West: return scorchSprite + PatchWest;
                default: return null;
            }
        }

        // ── Where a hit mark lands ───────────────────────────────────────────────────────────────
        //
        // `Location.hitTile(tile, damage, X, Y, type)` is only ever handed a point INSIDE the tile's
        // solid rect. That is not a convention, it is the hit test itself — `X >= phX1 && X <= phX2 &&
        // Y >= phY1 && Y <= phY2` (`weapon/Bullet.as:476`) — and the caller is a bullet whose centre
        // has already crossed into the cell. The port's swept contact is the other end of the same
        // event: it is where the projectile's SHAPE touches the tile, i.e. exactly on the face, and for
        // a long capsule tens of pixels outside it. Stamped there, a hole straddles the seam between
        // two tiles — half of it burns the neighbour, which is what "guns leave holes outside a tile"
        // looked like on screen.
        //
        // So a hit point is projected into the tile it struck and pushed `depth` inside that rect.
        // `depth` is not a fudge: AS3 advances a bullet in sub-steps of at most `World.maxdelta` = 9 px
        // and tests the tile after each one (`World.as:52`, loop at `Bullet.as:199-205`), so the centre
        // it stamps with is 0..9 px past the face, uniformly. Rolling it reproduces both halves of the
        // oracle's behaviour — the guarantee (never outside the tile) and the scatter (holes along one
        // wall do not stack on a single pixel).

        /// <summary>
        /// AS3 <c>World.maxdelta</c> — the bullet sub-step in pixels (<c>World.as:52</c>). A hit mark
        /// lands at most this far inside the tile it struck.
        /// </summary>
        public const float MaxImpactDepthPx = 9f;

        /// <summary>A point in room pixels, so the placement rule can be pinned without Unity.</summary>
        public readonly struct TilePointPx
        {
            public readonly float X;
            public readonly float Y;

            public TilePointPx(float x, float y)
            {
                X = x;
                Y = y;
            }
        }

        /// <summary>
        /// Projects <paramref name="hit"/> into the tile's solid rect and pushes it
        /// <paramref name="depthPx"/> inside, so a mark always lands <b>on the tile it struck</b>.
        ///
        /// <para>The caller passes the rect in the same unit as the point — room pixels for the oracle's
        /// <c>phX1..phX2</c>, Unity units on the view side. Nothing here assumes either.</para>
        ///
        /// <para><b>A negative depth is treated as zero, never as an outward push.</b> That would put the
        /// mark back on the seam, which is the defect this rule exists to remove.</para>
        /// </summary>
        /// <param name="hit">Where the hit was detected — the swept contact, in general outside the rect.</param>
        /// <param name="solidMin">Bottom-left corner of the tile's solid rect.</param>
        /// <param name="solidMax">Top-right corner of the same rect.</param>
        /// <param name="depthPx">How far inside the rect the mark belongs. 0..<see cref="MaxImpactDepthPx"/>.</param>
        public static TilePointPx PlaceInsideTile(TilePointPx hit, TilePointPx solidMin,
                                                  TilePointPx solidMax, float depthPx)
        {
            if (depthPx < 0f) depthPx = 0f;

            return new TilePointPx(
                ClampInto(hit.X, solidMin.X, solidMax.X, depthPx),
                ClampInto(hit.Y, solidMin.Y, solidMax.Y, depthPx));
        }

        /// <summary>
        /// Clamps one coordinate into <c>[min + depth, max - depth]</c>, collapsing to the centre when
        /// the span is narrower than twice the depth.
        /// </summary>
        private static float ClampInto(float value, float min, float max, float depth)
        {
            float lo = min + depth;
            float hi = max - depth;

            // A quarter tile is 10 px tall and a half tile 20 px, so a 9 px inset can invert the span.
            // The centre is then the only honest answer: it is inside, and it is the middle of the
            // solid part, which is where the builders already anchor the tile object.
            if (lo >= hi) return (min + max) * 0.5f;

            if (value < lo) return lo;
            if (value > hi) return hi;
            return value;
        }

        // ── Particle ids, exactly the <part id=…> rows in AllData.as ────────────────────────────
        public const string PartFake = "fake";
        public const string PartPole = "pole";
        public const string PartMetal = "metal";
        public const string PartKusok = "kusok";
        public const string PartKusokB = "kusokB";
        public const string PartKusokD = "kusokD";
        public const string PartSchep = "schep";
        public const string PartSteklo = "steklo";
        public const string PartPlav = "plav";
        public const string PartBur = "bur";
        public const string PartKusoch = "kusoch";
        public const string PartKusochB = "kusochB";
        public const string PartSchepoch = "schepoch";

        /// <summary>
        /// A hit that <b>damaged but did not destroy</b> a tile — AS3 <c>Grafon.dyrka(x, y, type, mat,
        /// blocked, ratio)</c>.
        /// </summary>
        /// <param name="decalType">
        /// The weapon's <c>tipdec</c>, already through <c>Location.hitTile</c>'s rewrite
        /// (<c>100</c> becomes <c>4</c>). <c>0</c> draws nothing — <c>Grafon.as:1064</c>.
        /// </param>
        /// <param name="material">
        /// AS3 <c>Tile.mat</c> — <b>the raw <c>&lt;mat mat='N'&gt;</c> index, not the port's
        /// <c>MaterialType</c> enum.</b> The two disagree from 2 upward: AS3 2 is concrete and
        /// <c>MaterialType.Wood</c> is 2, AS3 3 is wood and <c>MaterialType.Stone</c> is 3, and AS3 5/6/7
        /// have no enum value at all. <c>TileData.material</c> stores the raw index (the decoder casts
        /// <c>(MaterialType)form.mat</c>), so <c>(int)tile.material</c> is the correct argument — see
        /// <c>RoomEditorWindow.As3MaterialLabel</c>, which documents the same mismatch. <c>0</c> draws
        /// nothing — <c>Grafon.as:1064</c>.
        /// </param>
        /// <param name="blocked">
        /// AS3 <c>param5</c>: the hit was refused (indestructible, below <c>thre</c>, or a
        /// <c>desoff</c> room above 500 hp). Blocked hits draw the "soft" variant and never emit debris,
        /// because the surface did not actually give way.
        /// </param>
        /// <param name="damageRatio">
        /// AS3 <c>param2 / param1.hp</c> — <b>damage over the hp the tile has left after the hit</b>, not
        /// over its maximum. Only three arms read it (the three <c>rand * 0.5 &lt; ratio</c> gates), and
        /// they use it as "did this hit bite hard enough to throw a scorch as well as a hole".
        /// </param>
        /// <param name="scaleRoll">Uniform 0..1. Becomes <c>0.5 + roll * 0.5</c> — AS3 <c>:1063</c>.</param>
        /// <param name="rotationRoll">Uniform 0..1. Becomes <c>roll * 360</c> radians — AS3 <c>:1064</c>.</param>
        /// <param name="gateRoll">Uniform 0..1. The single gate roll — see the type doc.</param>
        public static TileDecalResult Resolve(
            int decalType, int material, bool blocked, float damageRatio,
            float scaleRoll, float rotationRoll, float gateRoll)
        {
            var result = new TileDecalResult
            {
                Scale = 1f,
                Blend = TileDecalBlend.Normal,
            };

            // Grafon.as:1064 — `if(param3 == 0 || param4 == 0) return;`
            if (decalType == 0 || material == 0)
            {
                return result;
            }

            // Grafon.as:1063-1064. Note that the scale is computed even for arms that never use it.
            float scale = scaleRoll * 0.5f + 0.5f;
            float rotation = rotationRoll * 360f;

            string erase = null;
            string scorch = null;
            var blend = TileDecalBlend.Normal;
            string particle = null;
            int particleCount = 0;

            // ── The material table — Grafon.as:1067-1181 ────────────────────────────────────────
            if (material == 1)
            {
                // Metal: a bullet hole and nothing else. No erase sprite at all — a metal wall dents,
                // it does not get punched through.
                if (decalType >= 1 && decalType <= 6)
                {
                    scorch = BulletMetal;
                }
                else if (decalType == 9)
                {
                    if (!blocked && gateRoll * 0.5f < damageRatio)
                    {
                        scorch = MetalTre;
                    }
                }
            }
            else if (material == 2 || material == 4 || material == 6)
            {
                // Concrete (2), brick (4), dirt (6) — one arm for all three, and the only difference
                // is which debris particle is emitted at the end.
                if (decalType >= 1 && decalType <= 3)
                {
                    // The hole appears only sometimes, and only above type 1: `param3 > 1 &&
                    // Math.random() > 0.5`. A type-1 hit is a pinprick with no hole.
                    if (decalType > 1 && gateRoll > 0.5f)
                    {
                        erase = BulletDyr;
                    }
                    scorch = BulletTre;
                    if (decalType == 2) scale += 0.5f;
                    if (decalType == 3) scale += 1f;
                }
                else if (decalType >= 4 && decalType <= 6)
                {
                    if (!blocked) scorch = PunchTre;
                    if (decalType == 5) scale += 0.5f;
                    if (decalType == 6) scale += 1f;
                }
                else if (decalType == 9)
                {
                    if (!blocked && gateRoll * 0.5f < damageRatio)
                    {
                        scorch = ExplTre;
                    }
                }

                // `if(param3 < 10 && !param5)` — a refused hit throws no chips, whatever its type.
                if (decalType < 10 && !blocked)
                {
                    particle = material == 2 ? PartKusoch : PartKusochB;
                    particleCount = 3;
                }
            }
            else if (material == 3)
            {
                // Wood. Always a hole, always the same splinter mark, and **no rotation** — a plank
                // splinters along its grain, so AS3 pins `_loc12_ = 0` (`:1141`).
                if (decalType >= 1 && decalType <= 3)
                {
                    erase = BulletDyr;
                    scorch = BulletWood;
                    rotation = 0f;
                    if (decalType == 2) scale += 0.5f;
                    if (decalType == 3) scale += 1f;
                }
                else if (decalType >= 4 && decalType <= 6)
                {
                    if (!blocked) scorch = PunchTre;
                    if (decalType == 5) scale += 0.5f;
                    if (decalType == 6) scale += 1f;
                }
                else if (decalType == 9)
                {
                    if (!blocked && gateRoll * 0.5f < damageRatio)
                    {
                        scorch = ExplTre;
                    }
                }

                if (decalType < 10 && !blocked)
                {
                    particle = PartSchepoch;
                    particleCount = 3;
                }
            }
            else if (material == 7)
            {
                // Pipes and poles: no decal at all, just five pole shards (`:1179-1181`).
                particle = PartPole;
                particleCount = 5;
            }

            // ── The type table — Grafon.as:1183-1258. Runs *after* the material table and assigns the
            //    same two sprite slots, so the two are additive rather than exclusive: the debris a
            //    material arm chose survives a type arm (a plasma hit on a pipe throws pole shards).
            if (decalType == 11)
            {
                // Fire: only one hit in ten leaves a mark at all.
                if (gateRoll < 0.1f)
                {
                    scorch = FireSoft;
                }
            }
            else if (decalType == 12 || decalType == 13)
            {
                // Laser. A blocked hit mostly still burns (the beam heats the surface without
                // penetrating) — `rand * 0.2 > ratio` is the escape into fire_soft, so a *heavier*
                // hit is more likely to keep the laser mark.
                if (blocked && gateRoll * 0.2f > damageRatio)
                {
                    scorch = FireSoft;
                }
                else
                {
                    scorch = LaserTre;
                }
                blend = TileDecalBlend.Hardlight;
                if (decalType == 13) scale *= 0.6f;
            }
            else if (decalType == 15)
            {
                // Plasma: a soft glow when the surface held, a hole plus a burn when it did not.
                if (blocked)
                {
                    scorch = PlasmaSoft;
                }
                else
                {
                    erase = PlasmaDyr;
                    scorch = PlasmaTre;
                }
                blend = TileDecalBlend.Hardlight;
            }
            else if (decalType == 16)
            {
                // Blue plasma. **Blocked falls back to fire_soft, not to a soft plasma** — the oracle
                // has no bluplasma_soft symbol, and `:1219-1228` reuses the fire one.
                if (blocked)
                {
                    scorch = FireSoft;
                }
                else
                {
                    erase = PlasmaDyr;
                    scorch = BluPlasmaTre;
                }
                blend = TileDecalBlend.Hardlight;
            }
            else if (decalType == 17)
            {
                if (blocked)
                {
                    scorch = FireSoft;
                }
                else
                {
                    erase = PlasmaDyr;
                    scorch = PinkPlasmaTre;
                }
                blend = TileDecalBlend.Hardlight;
            }
            else if (decalType == 18)
            {
                // Cryo is the one energy decal with no hole and no ratio test.
                scorch = CryoSoft;
                blend = TileDecalBlend.Hardlight;
            }
            else if (decalType == 19)
            {
                // Pink explosive plasma. **Normal blend**, unlike every other energy arm — `:1250-1258`
                // never touches `_loc9_`.
                if (!blocked && gateRoll * 0.5f < damageRatio)
                {
                    scorch = PlaExplTre;
                }
            }

            result.EraseSprite = erase;
            result.ScorchSprite = scorch;
            result.Scale = scale;
            result.RotationRadians = rotation;
            result.Blend = blend;
            result.ParticleId = particle;
            result.ParticleCount = particleCount;
            return result;
        }

        /// <summary>
        /// A tile that <b>died</b> — AS3 <c>Grafon.tileDie(tile, type)</c>
        /// (<c>Grafon.as:968-1053</c>).
        ///
        /// <para><b>A different table, not a special case of the first.</b> Death ignores
        /// <c>blocked</c> and <c>ratio</c> entirely (there is nothing left to refuse), always draws at
        /// scale 1 and rotation 0, and always blends hardlight. The debris particle is chosen by
        /// <i>material</i> while the decal is chosen by <i>type band</i>, and the two are independent:
        /// a concrete wall shot by a plasma weapon throws concrete chunks <b>and</b> leaves a melted
        /// scar.</para>
        /// </summary>
        /// <param name="decalType">The same <c>tipdec</c> the damaging hit carried.</param>
        /// <param name="material">The raw AS3 <c>mat</c> index — see <see cref="Resolve"/>.</param>
        /// <param name="fake">
        /// AS3 <c>Tile.fake</c> — a decorative tile that was never solid. It throws the "fake" puff and
        /// leaves a burnt block, and it does this <b>before</b> the material is consulted, so a fake
        /// pipe still reads as fake.
        /// </param>
        public static TileDecalResult ResolveDestroy(int decalType, int material, bool fake)
        {
            var result = new TileDecalResult
            {
                Scale = 1f,
                RotationRadians = 0f,
                Blend = TileDecalBlend.Hardlight,
                EraseSprite = BlockDyr,
                ScorchSprite = BlockTre,
            };

            if (fake)
            {
                result.ParticleId = PartFake;
                result.ParticleCount = 1;
                result.ScorchSprite = BlockBur;
                return result;
            }

            if (material == 7)
            {
                // Poles: the "fake" puff plus ten pole shards, and the erase is the plain TileMask —
                // a square hole, because there is no shaped pipe silhouette to carve.
                result.ParticleId = PartFake;
                result.ParticleCount = 1;
                result.ExtraParticleId = PartPole;
                result.ExtraParticleCount = 10;
                result.EraseSprite = TileMask;
                result.ScorchSprite = null;
                return result;
            }

            if (decalType < 10)
            {
                // Six chunks of whatever it was made of. mat 5 (glass) and 6 (dirt) included; mat 4
                // (brick) gets the heavy-chunk sprite.
                switch (material)
                {
                    case 1: result.ParticleId = PartMetal; break;
                    case 2: result.ParticleId = PartKusok; break;
                    case 3: result.ParticleId = PartSchep; break;
                    case 4: result.ParticleId = PartKusokB; break;
                    case 5: result.ParticleId = PartSteklo; break;
                    case 6: result.ParticleId = PartKusokD; break;
                    default: result.ParticleId = null; break;
                }
                result.ParticleCount = 6;
                return result;
            }

            if (decalType >= 15)
            {
                // Plasma and up: melted. The erase and the scar both change — this is the only arm
                // that replaces block_dyr.
                result.ParticleId = PartPlav;
                result.ParticleCount = 1;
                result.EraseSprite = BlockPlav;
                result.ScorchSprite = BlockPla;
                return result;
            }

            if (decalType >= 11 && decalType <= 13)
            {
                // Burned out rather than blown out.
                result.ParticleId = PartBur;
                result.ParticleCount = 1;
                result.ScorchSprite = BlockBur;
                return result;
            }

            // Types 10 and 14 land here with the defaults: block_dyr + block_tre, no debris at all.
            return result;
        }
    }
}
