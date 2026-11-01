using NUnit.Framework;
using PFE.Systems.Map;

namespace PFE.Tests.EditMode.Systems.Map
{
    /// <summary>
    /// Pins <see cref="TileDecalRules.Resolve"/> and <see cref="TileDecalRules.ResolveDestroy"/>:
    /// every material/decal-type arm, every blend mode, every particle, and the roll ranges.
    ///
    /// <para>These are pure functions: no Unity state, no scene, no rendering. A failure is a wrong
    /// string or a wrong number, and the oracle's strings are literal sprite class names from
    /// <c>Grafon.as:1067-1258</c>.</para>
    ///
    /// <para><b>Material numbers are AS3 raw <c>mat</c> indices</b>, not the port's
    /// <see cref="MaterialType"/> enum names. AS3 <c>mat='2'</c> is concrete/stone, <c>mat='3'</c>
    /// is wood, <c>mat='4'</c> is brick, <c>mat='5'</c> is glass, <c>mat='6'</c> is dirt. The port's
    /// enum has Wood=2 and Stone=3, which is a label mismatch that does not affect runtime because
    /// <see cref="TileDecoder"/> casts the raw XML value directly.</para>
    ///
    /// <para><b>Decal-type numbers are the oracle's <c>tipdec</c> integers, and the bands matter.</b>
    /// <c>1..3</c> is the bullet ladder, <c>4..6</c> the impact ladder, <c>9</c> explosion, <c>11</c>
    /// fire, <c>12/13</c> laser/beam, <c>15/16/17</c> plasma/blue/pink, <c>18</c> cryo, <c>19</c> pink
    /// explosive plasma. An earlier revision of this file asserted fire on <c>9</c>, laser on <c>8</c>
    /// and cryo on <c>10</c> — none of those arms exist, so it was pinning a table that only the test
    /// believed in.</para>
    /// </summary>
    public sealed class TileDecalRulesTests
    {
        // AS3 material indices, named so a failure message reads as data rather than as a number.
        private const int Metal = 1, Concrete = 2, Wood = 3, Brick = 4, Glass = 5, Dirt = 6, Pipes = 7;

        private static TileDecalResult Dyrka(int decalType, int material, bool blocked = false,
                                             float ratio = 0.5f, float scaleRoll = 0f,
                                             float rotRoll = 0f, float gateRoll = 0f)
        {
            return TileDecalRules.Resolve(decalType, material, blocked, ratio, scaleRoll, rotRoll, gateRoll);
        }

        // ── Null / zero guards ────────────────────────────────────────────────

        [Test]
        public void Resolve_ZeroDecalType_ReturnsEmpty()
        {
            var r = Dyrka(0, Metal);
            Assert.IsNull(r.EraseSprite);
            Assert.IsNull(r.ScorchSprite);
            Assert.IsNull(r.ParticleId);
            Assert.AreEqual(1f, r.Scale);
            Assert.IsTrue(r.IsEmpty);
        }

        [Test]
        public void Resolve_ZeroMaterial_ReturnsEmpty()
        {
            var r = Dyrka(1, material: 0);
            Assert.IsNull(r.EraseSprite);
            Assert.IsNull(r.ScorchSprite);
        }

        // ── Material 1: Metal ─────────────────────────────────────────────────

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        public void Resolve_Metal_AnyTypeOneToSix_IsBulletMetalWithNoHole(int decalType)
        {
            var r = Dyrka(decalType, Metal);
            Assert.AreEqual("bullet_metal", r.ScorchSprite, "the metal arm answers 1..6 alike");
            Assert.IsNull(r.EraseSprite, "metal dents, it does not get punched through");
            Assert.AreEqual(TileDecalBlend.Normal, r.Blend);
        }

        [Test]
        public void Resolve_MetalExplosion_RatioGatesTheScorch()
        {
            var opened = Dyrka(9, Metal, ratio: 0.9f, gateRoll: 0f);   // 0 * 0.5 < 0.9
            Assert.AreEqual("metal_tre", opened.ScorchSprite);

            var shut = Dyrka(9, Metal, ratio: 0.1f, gateRoll: 1f);     // 0.5 < 0.1 is false
            Assert.IsNull(shut.ScorchSprite);
        }

        [Test]
        public void Resolve_MetalExplosion_Blocked_NeverDraws()
        {
            var r = Dyrka(9, Metal, blocked: true, ratio: 1f, gateRoll: 0f);
            Assert.IsNull(r.ScorchSprite);
        }

        // ── Material 2: Concrete ──────────────────────────────────────────────

        [Test]
        public void Resolve_ConcreteType1_HasScorchAndNoHole()
        {
            var r = Dyrka(1, Concrete);
            Assert.IsNull(r.EraseSprite, "type 1 is a pinprick: the hole gate needs type > 1");
            Assert.AreEqual("bullet_tre", r.ScorchSprite);
        }

        [Test]
        public void Resolve_ConcreteType2_HoleOpensAboveHalfRoll()
        {
            var r = Dyrka(2, Concrete, gateRoll: 0.51f);
            Assert.AreEqual("bullet_dyr", r.EraseSprite);
            Assert.AreEqual("bullet_tre", r.ScorchSprite);
            Assert.AreEqual(1.0f, r.Scale, 0.001f, "base 0.5 roll + 0.5 for type 2");
        }

        [Test]
        public void Resolve_ConcreteType2_HoleStaysShutAtHalfRoll()
        {
            var r = Dyrka(2, Concrete, gateRoll: 0.5f);
            Assert.IsNull(r.EraseSprite, "the gate is a strict `> 0.5`");
        }

        [Test]
        public void Resolve_ConcreteType3_IsTheLargestBulletHole()
        {
            var r = Dyrka(3, Concrete);
            Assert.AreEqual("bullet_tre", r.ScorchSprite);
            Assert.AreEqual(1.5f, r.Scale, 0.001f, "base 0.5 + 1 for type 3");
        }

        [TestCase(4, 0.5f)]
        [TestCase(5, 1.0f)]
        [TestCase(6, 1.5f)]
        public void Resolve_Concrete_ImpactLadder_UsesPunchTreAndScales(int decalType, float scale)
        {
            var r = Dyrka(decalType, Concrete, scaleRoll: 0f);
            Assert.AreEqual("punch_tre", r.ScorchSprite);
            Assert.IsNull(r.EraseSprite, "an impact mark never punches a hole");
            Assert.AreEqual(scale, r.Scale, 0.001f);
        }

        [Test]
        public void Resolve_Concrete_BlockedImpact_DrawsNothing()
        {
            var r = Dyrka(4, Concrete, blocked: true);
            Assert.IsNull(r.ScorchSprite);
        }

        [Test]
        public void Resolve_ConcreteExplosion_UnblockedHighRatio_HasExplTre()
        {
            var r = Dyrka(9, Concrete, ratio: 0.9f, gateRoll: 0f);
            Assert.AreEqual("expl_tre", r.ScorchSprite);
        }

        [Test]
        public void Resolve_ConcreteExplosion_Blocked_DrawsNothing()
        {
            var r = Dyrka(9, Concrete, blocked: true, ratio: 1f, gateRoll: 0f);
            Assert.IsNull(r.ScorchSprite);
        }

        [Test]
        public void Resolve_ConcreteChips_ThreeOfThem_AndNoneWhenBlocked()
        {
            var hit = Dyrka(1, Concrete);
            Assert.AreEqual("kusoch", hit.ParticleId);
            Assert.AreEqual(3, hit.ParticleCount);

            var refused = Dyrka(1, Concrete, blocked: true);
            Assert.IsNull(refused.ParticleId, "a refused hit throws no chips");
        }

        [Test]
        public void Resolve_BrickAndDirt_ThrowTheHeavyChip()
        {
            Assert.AreEqual("kusochB", Dyrka(1, Brick).ParticleId);
            Assert.AreEqual("kusochB", Dyrka(1, Dirt).ParticleId);
        }

        [Test]
        public void Resolve_Glass_HasNoArmAtAll()
        {
            // `dyrka`'s material table branches on 1, 2|4|6, 3 and 7 — mat 5 (glass) is in none of
            // them, so a bullet through a window leaves nothing. Only `tileDie` knows glass (`steklo`).
            var r = Dyrka(1, Glass);
            Assert.IsNull(r.ScorchSprite);
            Assert.IsNull(r.EraseSprite);
            Assert.IsNull(r.ParticleId);
        }

        // ── Material 3: Wood ──────────────────────────────────────────────────

        [Test]
        public void Resolve_Wood_HasBothSpritesAndNoRotation()
        {
            var r = Dyrka(1, Wood, rotRoll: 1f);
            Assert.AreEqual("bullet_dyr", r.EraseSprite, "wood always punches a hole");
            Assert.AreEqual("bullet_wood", r.ScorchSprite);
            Assert.AreEqual(0f, r.RotationRadians, 0.001f, "a plank splinters along its grain");
        }

        [Test]
        public void Resolve_WoodBlocked_StillDrawsBothAndWithholdsOnlyTheChips()
        {
            // The wood arm is the one material arm that never tests `param5` (`Grafon.as:1141-1172`):
            // a refused hit on a plank still draws the hole and the splinter mark.
            var r = Dyrka(1, Wood, blocked: true);
            Assert.AreEqual("bullet_dyr", r.EraseSprite);
            Assert.AreEqual("bullet_wood", r.ScorchSprite);
            Assert.IsNull(r.ParticleId);
        }

        [Test]
        public void Resolve_WoodExplosion_ThrowsSchepoch()
        {
            var r = Dyrka(9, Wood, ratio: 1f, gateRoll: 0f);
            Assert.AreEqual("expl_tre", r.ScorchSprite);
            Assert.AreEqual("schepoch", r.ParticleId);
            Assert.AreEqual(3, r.ParticleCount);
        }

        // ── Material 7: Pipes / poles ─────────────────────────────────────────

        [Test]
        public void Resolve_PipeHit_OnlyPoleShards()
        {
            var r = Dyrka(1, Pipes);
            Assert.IsNull(r.EraseSprite);
            Assert.IsNull(r.ScorchSprite);
            Assert.AreEqual("pole", r.ParticleId);
            Assert.AreEqual(5, r.ParticleCount);
        }

        [Test]
        public void Resolve_PlasmaOnAPipe_KeepsTheShardsUnderThePlasmaMark()
        {
            // The type table runs last and assigns only the two sprite slots, so the material arm's
            // debris survives it. This is the one place both tables are visibly at work at once.
            var r = Dyrka(15, Pipes);
            Assert.AreEqual("plasma_dyr", r.EraseSprite);
            Assert.AreEqual("plasma_tre", r.ScorchSprite);
            Assert.AreEqual("pole", r.ParticleId);
            Assert.AreEqual(5, r.ParticleCount);
            Assert.AreEqual(TileDecalBlend.Hardlight, r.Blend);
        }

        // ── Type 11: fire ─────────────────────────────────────────────────────

        [Test]
        public void Resolve_Fire_OnlyOneHitInTenLeavesAMark()
        {
            Assert.AreEqual("fire_soft", Dyrka(11, Concrete, gateRoll: 0.09f).ScorchSprite);
            Assert.IsNull(Dyrka(11, Concrete, gateRoll: 0.1f).ScorchSprite, "the gate is a strict `< 0.1`");
        }

        [Test]
        public void Resolve_Fire_IsNormalBlend()
        {
            // Fire is the one type-11+ arm that never touches `_loc9_`.
            Assert.AreEqual(TileDecalBlend.Normal, Dyrka(11, Concrete, gateRoll: 0f).Blend);
        }

        // ── Types 12/13: laser and beam ───────────────────────────────────────

        [Test]
        public void Resolve_Laser_Unblocked_IsLaserTreHardlight()
        {
            var r = Dyrka(12, Metal);
            Assert.AreEqual("laser_tre", r.ScorchSprite);
            Assert.AreEqual(TileDecalBlend.Hardlight, r.Blend);
        }

        [Test]
        public void Resolve_Laser_BlockedHeavyHit_KeepsTheLaserMark()
        {
            // `random * 0.2 > ratio` is the escape into fire, so a heavy hit (high ratio) stays a laser.
            var r = Dyrka(12, Metal, blocked: true, ratio: 0.9f, gateRoll: 0f);
            Assert.AreEqual("laser_tre", r.ScorchSprite);
        }

        [Test]
        public void Resolve_Laser_BlockedLightHit_EscapesToFire()
        {
            var r = Dyrka(12, Metal, blocked: true, ratio: 0.1f, gateRoll: 1f);   // 0.2 > 0.1
            Assert.AreEqual("fire_soft", r.ScorchSprite);
        }

        [Test]
        public void Resolve_Beam_IsSixtyPercentScale()
        {
            var r = Dyrka(13, Metal, scaleRoll: 1f);   // 1.0 before the multiplier
            Assert.AreEqual("laser_tre", r.ScorchSprite);
            Assert.AreEqual(0.6f, r.Scale, 0.001f);
        }

        // ── Types 15..18: the energy family ───────────────────────────────────

        [Test]
        public void Resolve_Plasma_Unblocked_HolePlusBurn()
        {
            var r = Dyrka(15, Concrete);
            Assert.AreEqual("plasma_dyr", r.EraseSprite);
            Assert.AreEqual("plasma_tre", r.ScorchSprite);
            Assert.AreEqual(TileDecalBlend.Hardlight, r.Blend);
        }

        [Test]
        public void Resolve_Plasma_Blocked_IsSoftWithNoHole()
        {
            var r = Dyrka(15, Concrete, blocked: true);
            Assert.IsNull(r.EraseSprite);
            Assert.AreEqual("plasma_soft", r.ScorchSprite);
        }

        [TestCase(16, "bluplasma_tre")]
        [TestCase(17, "pinkplasma_tre")]
        public void Resolve_ColouredPlasma_Unblocked_HolePlusItsOwnBurn(int decalType, string scorch)
        {
            var r = Dyrka(decalType, Concrete);
            Assert.AreEqual("plasma_dyr", r.EraseSprite);
            Assert.AreEqual(scorch, r.ScorchSprite);
            Assert.AreEqual(TileDecalBlend.Hardlight, r.Blend);
        }

        [TestCase(16)]
        [TestCase(17)]
        public void Resolve_ColouredPlasma_Blocked_FallsBackToFire(int decalType)
        {
            // There is no bluplasma_soft or pinkplasma_soft symbol; `:1219-1240` reuses fire_soft.
            var r = Dyrka(decalType, Concrete, blocked: true);
            Assert.AreEqual("fire_soft", r.ScorchSprite);
            Assert.IsNull(r.EraseSprite);
        }

        [Test]
        public void Resolve_Cryo_IsASoftMarkWithNoHoleAndNoGate()
        {
            var r = Dyrka(18, Metal, blocked: true, ratio: 0f, gateRoll: 1f);
            Assert.AreEqual("cryo_soft", r.ScorchSprite);
            Assert.IsNull(r.EraseSprite);
            Assert.AreEqual(TileDecalBlend.Hardlight, r.Blend);
        }

        // ── Type 19: pink explosive plasma ────────────────────────────────────

        [Test]
        public void Resolve_PinkExplosive_RatioGated_AndNormalBlend()
        {
            var opened = Dyrka(19, Concrete, ratio: 0.9f, gateRoll: 0f);
            Assert.AreEqual("plaexpl_tre", opened.ScorchSprite);
            Assert.AreEqual(TileDecalBlend.Normal, opened.Blend,
                "the only energy type that is not hardlight");

            var shut = Dyrka(19, Concrete, ratio: 0.1f, gateRoll: 1f);
            Assert.IsNull(shut.ScorchSprite);
        }

        [Test]
        public void Resolve_PinkExplosive_Blocked_NeverDraws()
        {
            Assert.IsNull(Dyrka(19, Concrete, blocked: true, ratio: 1f, gateRoll: 0f).ScorchSprite);
        }

        // ── Scale / rotation rolls ────────────────────────────────────────────

        [Test]
        public void Resolve_ScaleRollZero_GivesHalfScale()
        {
            Assert.AreEqual(0.5f, Dyrka(1, Concrete, scaleRoll: 0f).Scale, 0.001f);
        }

        [Test]
        public void Resolve_ScaleRollOne_GivesFullScale()
        {
            Assert.AreEqual(1.0f, Dyrka(1, Concrete, scaleRoll: 1f).Scale, 0.001f);
        }

        [Test]
        public void Resolve_RotationRollIsRadians()
        {
            Assert.AreEqual(0f, Dyrka(1, Concrete, rotRoll: 0f).RotationRadians, 0.001f);
            Assert.AreEqual(360f, Dyrka(1, Concrete, rotRoll: 1f).RotationRadians, 0.001f,
                "AS3 hands `random * 360` to Matrix.rotate(), which takes radians");
        }

        // ── ResolveDestroy (tileDie) ──────────────────────────────────────────

        [Test]
        public void ResolveDestroy_Defaults_AreBlockDyrOverBlockTre_AtScaleOne()
        {
            var r = TileDecalRules.ResolveDestroy(decalType: 10, material: Concrete, fake: false);
            Assert.AreEqual("block_dyr", r.EraseSprite);
            Assert.AreEqual("block_tre", r.ScorchSprite);
            Assert.AreEqual(1f, r.Scale, 0.001f);
            Assert.AreEqual(0f, r.RotationRadians, 0.001f);
            Assert.AreEqual(TileDecalBlend.Hardlight, r.Blend, "every death decal is hardlight");
            Assert.IsNull(r.ParticleId, "types 10 and 14 fall through with no debris");
        }

        [TestCase(1, "metal")]
        [TestCase(2, "kusok")]
        [TestCase(3, "schep")]
        [TestCase(4, "kusokB")]
        [TestCase(5, "steklo")]
        [TestCase(6, "kusokD")]
        public void ResolveDestroy_LowType_ThrowsSixChunksOfTheMaterial(int material, string particle)
        {
            var r = TileDecalRules.ResolveDestroy(decalType: 1, material: material, fake: false);
            Assert.AreEqual(particle, r.ParticleId);
            Assert.AreEqual(6, r.ParticleCount);
            Assert.AreEqual("block_dyr", r.EraseSprite);
            Assert.AreEqual("block_tre", r.ScorchSprite);
        }

        [Test]
        public void ResolveDestroy_Type7OnConcrete_StillTakesTheChunkArm()
        {
            // The sprite arms are `< 10`, `>= 15` and `11..13`; 7 and 8 fall into the first and throw
            // chunks like any low type. (On a pipe, mat 7 wins first — see the pipe fixture.)
            var r = TileDecalRules.ResolveDestroy(decalType: 7, material: Concrete, fake: false);
            Assert.AreEqual("kusok", r.ParticleId);
            Assert.AreEqual(6, r.ParticleCount);
            Assert.AreEqual("block_dyr", r.EraseSprite);
            Assert.AreEqual("block_tre", r.ScorchSprite);
        }

        [TestCase(11)]
        [TestCase(12)]
        [TestCase(13)]
        public void ResolveDestroy_BurnTypes_AreBurnedOutNotBlownOut(int decalType)
        {
            var r = TileDecalRules.ResolveDestroy(decalType, Concrete, fake: false);
            Assert.AreEqual("bur", r.ParticleId);
            Assert.AreEqual(1, r.ParticleCount);
            Assert.AreEqual("block_bur", r.ScorchSprite);
            Assert.AreEqual("block_dyr", r.EraseSprite, "the erase sprite is unchanged here");
        }

        [TestCase(15)]
        [TestCase(16)]
        [TestCase(17)]
        [TestCase(18)]
        [TestCase(19)]
        public void ResolveDestroy_PlasmaAndUp_IsMelted(int decalType)
        {
            var r = TileDecalRules.ResolveDestroy(decalType, Concrete, fake: false);
            Assert.AreEqual("plav", r.ParticleId);
            Assert.AreEqual(1, r.ParticleCount);
            Assert.AreEqual("block_plav", r.EraseSprite, "the only arm that replaces block_dyr");
            Assert.AreEqual("block_pla", r.ScorchSprite);
        }

        [Test]
        public void ResolveDestroy_Pipe_IsAFakeSquareHoleAndTenShards()
        {
            var r = TileDecalRules.ResolveDestroy(decalType: 1, material: Pipes, fake: false);
            Assert.AreEqual("TileMask", r.EraseSprite);
            Assert.IsNull(r.ScorchSprite);
            Assert.AreEqual("fake", r.ParticleId);
            Assert.AreEqual("pole", r.ExtraParticleId);
            Assert.AreEqual(10, r.ExtraParticleCount);
        }

        [Test]
        public void ResolveDestroy_Fake_IsCheckedBeforeTheMaterial()
        {
            var r = TileDecalRules.ResolveDestroy(decalType: 15, material: Pipes, fake: true);
            Assert.AreEqual("block_bur", r.ScorchSprite);
            Assert.AreEqual("block_dyr", r.EraseSprite);
            Assert.AreEqual("fake", r.ParticleId);
            Assert.AreEqual(1, r.ParticleCount);
            Assert.IsNull(r.ExtraParticleId, "a fake pipe is still a fake first");
        }

        // ── Death-scorch patches ──────────────────────────────────────────────
        //
        // A death scorch is 71x72 on a 40x40 cell, so AS3 clips it against the destination alpha: only
        // the rim that lands on a still-solid neighbour survives. The port pre-cuts it into four
        // cell-sized pieces (bake_tile_decal_patches.py); these pin the names the layer asks for, since
        // a mismatch is silent — the patch never loads and the debris simply is not there.

        [Test]
        public void PatchSides_AreFourDistinctBits()
        {
            Assert.AreEqual(1, (int)TileNeighbourSides.North);
            Assert.AreEqual(2, (int)TileNeighbourSides.South);
            Assert.AreEqual(4, (int)TileNeighbourSides.East);
            Assert.AreEqual(8, (int)TileNeighbourSides.West);
            Assert.AreEqual(0, (int)TileNeighbourSides.None);

            Assert.AreEqual(TileNeighbourSides.All,
                TileNeighbourSides.North | TileNeighbourSides.South |
                TileNeighbourSides.East | TileNeighbourSides.West);
        }

        [TestCase(TileNeighbourSides.North, "block_tre_n")]
        [TestCase(TileNeighbourSides.South, "block_tre_s")]
        [TestCase(TileNeighbourSides.East, "block_tre_e")]
        [TestCase(TileNeighbourSides.West, "block_tre_w")]
        public void PatchSprite_NamesTheNeighbourCell(TileNeighbourSides side, string expected)
        {
            Assert.AreEqual(expected, TileDecalRules.PatchSprite("block_tre", side));
        }

        [Test]
        public void PatchSprite_NoScorchDrawsNothing()
        {
            Assert.IsNull(TileDecalRules.PatchSprite(null, TileNeighbourSides.North));
            Assert.IsNull(TileDecalRules.PatchSprite(string.Empty, TileNeighbourSides.East));
        }

        [Test]
        public void PatchSprite_IsNotACombination()
        {
            Assert.IsNull(TileDecalRules.PatchSprite("block_tre", TileNeighbourSides.None));
            Assert.IsNull(TileDecalRules.PatchSprite("block_tre", TileNeighbourSides.All));
            Assert.IsNull(TileDecalRules.PatchSprite("block_tre",
                TileNeighbourSides.North | TileNeighbourSides.East));
        }

        // ── Where a hit mark lands ───────────────────────────────────────────────
        //
        // AS3 only ever calls `hitTile` with a point inside the tile's solid rect — the hit test IS
        // `X >= phX1 && X <= phX2 && Y >= phY1 && Y <= phY2` (`weapon/Bullet.as:476`) — and the caller
        // is a bullet advanced in sub-steps of at most `World.maxdelta` = 9 px. The port hands over the
        // swept *surface* contact, which sits on the seam; that is what put holes outside a tile.

        [Test]
        public void MaxImpactDepth_IsTheOracleSubstep()
        {
            // World.as:52 — `public static const maxdelta:* = 9`.
            Assert.AreEqual(9f, TileDecalRules.MaxImpactDepthPx);
        }

        [Test]
        public void PlaceInsideTile_ContactOnTheLeftFaceGoesInside()
        {
            var p = Place(-12f, 20f, depth: 9f);

            Assert.AreEqual(9f, p.X, 0.001f, "a hit on the left face lands 9 px inside it");
            Assert.AreEqual(20f, p.Y, 0.001f, "and keeps its position along the face");
        }

        [Test]
        public void PlaceInsideTile_EveryFaceIsClampedOntoIt()
        {
            // Depth 0 is the degenerate end of the oracle's roll: the mark sits on the face, which is
            // still "inside" by AS3's inclusive test but not yet clear of the seam.
            Assert.AreEqual(0f, Place(-50f, 20f, depth: 0f).X, 0.001f);
            Assert.AreEqual(40f, Place(90f, 20f, depth: 0f).X, 0.001f);
            Assert.AreEqual(0f, Place(20f, -50f, depth: 0f).Y, 0.001f);
            Assert.AreEqual(40f, Place(20f, 90f, depth: 0f).Y, 0.001f);
        }

        [Test]
        public void PlaceInsideTile_NeverLandsOutsideTheTile()
        {
            var hits = new[]
            {
                new TileDecalRules.TilePointPx(-80f, 20f),
                new TileDecalRules.TilePointPx(120f, 20f),
                new TileDecalRules.TilePointPx(20f, -80f),
                new TileDecalRules.TilePointPx(20f, 120f),
                new TileDecalRules.TilePointPx(-80f, -80f),
                new TileDecalRules.TilePointPx(120f, 120f),
            };

            foreach (float depth in new[] { 0f, 4.5f, 9f })
            {
                foreach (var hit in hits)
                {
                    var p = TileDecalRules.PlaceInsideTile(
                        hit, new TileDecalRules.TilePointPx(0f, 0f),
                        new TileDecalRules.TilePointPx(40f, 40f), depth);

                    Assert.GreaterOrEqual(p.X, 0f, "depth " + depth);
                    Assert.LessOrEqual(p.X, 40f, "depth " + depth);
                    Assert.GreaterOrEqual(p.Y, 0f, "depth " + depth);
                    Assert.LessOrEqual(p.Y, 40f, "depth " + depth);
                }
            }
        }

        [Test]
        public void PlaceInsideTile_ADeepMarkKeepsItsOwnSpot()
        {
            var p = Place(20f, 20f, depth: 9f);

            Assert.AreEqual(20f, p.X, 0.001f);
            Assert.AreEqual(20f, p.Y, 0.001f);
        }

        [Test]
        public void PlaceInsideTile_ThinTileCollapsesToItsCentre()
        {
            // A quarter tile is 10 px tall, so a 9 px inset inverts the span. The centre is then the
            // only honest answer — it is inside, and it is the solid part's own middle.
            var p = TileDecalRules.PlaceInsideTile(
                new TileDecalRules.TilePointPx(2f, 1f),
                new TileDecalRules.TilePointPx(0f, 0f),
                new TileDecalRules.TilePointPx(40f, 10f),
                9f);

            Assert.AreEqual(9f, p.X, 0.001f);
            Assert.AreEqual(5f, p.Y, 0.001f);
        }

        [Test]
        public void PlaceInsideTile_NegativeDepthDoesNotPushOutward()
        {
            var p = Place(-10f, 20f, depth: -9f);

            Assert.AreEqual(0f, p.X, 0.001f, "a negative depth must not put the mark back on the seam");
        }

        private static TileDecalRules.TilePointPx Place(float hitX, float hitY, float depth)
        {
            return TileDecalRules.PlaceInsideTile(
                new TileDecalRules.TilePointPx(hitX, hitY),
                new TileDecalRules.TilePointPx(0f, 0f),
                new TileDecalRules.TilePointPx(40f, 40f),
                depth);
        }
    }
}
