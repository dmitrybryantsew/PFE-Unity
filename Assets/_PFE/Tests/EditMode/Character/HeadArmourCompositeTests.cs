using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Tests.EditMode.Character
{
    /// <summary>
    /// The per-armour head: every outfit must be drawn as layers, never as one baked
    /// <c>morda_armor</c>(273) frame.
    ///
    /// <para><b>Why this needs a guard.</b> The head is the one part of the rig the port does not
    /// take from the SWF verbatim. <c>morda_armor</c>(273) is the whole head assembly
    /// (<c>pfe_fla/morda_26.as</c> calls <c>Obj.setArmor(this)</c>), and the port re-implements it as
    /// separate parts. Every failure mode here is silent: a missing override simply draws the
    /// unarmoured head, which is what put the battle helmet on <c>pip</c> and left <c>power</c> with
    /// an eye and a horn and no head at all.</para>
    ///
    /// <para><b>Why a baked frame is a defect and not a shortcut.</b> The original composes the head
    /// in all sixteen frames: <c>eye</c>(220) is placed once, at depth 6, and never removed, so the
    /// eye is the live one — its state and gaze come from <c>UnitPlayer.as:4690-4713</c> and its
    /// colour from <c>Appear.trEye</c>. A baked frame freezes whatever frame of 220 the exporter
    /// happened to rasterise, and on the shipped art that is the wrong frame for four of the eight
    /// armours (<c>battle</c>, <c>intel</c>, <c>tre</c>, <c>spec</c>). The exporter also drifts the
    /// eye's <i>style</i> (215), so the baked eye is not even a consistent one.</para>
    ///
    /// <para><b>What is pinned and what is not.</b> Slot membership, non-null sprites and the draw
    /// order are durable: they encode the rule, not the art. Exact pivots and offsets are not
    /// pinned, because they move legitimately when the frames are re-exported at a different zoom.
    /// </para>
    /// </summary>
    [TestFixture]
    public class HeadArmourCompositeTests
    {
        const string DefinitionPath = "Characters/PlayerAnimationDefinition";

        /// <summary>
        /// The head block in the order <c>morda_armor</c>(273) draws it — ascending SWF placement
        /// depth, which is Flash's back-to-front order. Depth is what decides whether the shell is
        /// behind the eye (it is: 3 &lt; 6) and whether the second helm layer is in front of the
        /// first (it is: 27 &gt; 26), so this order is not cosmetic.
        /// </summary>
        static readonly (string part, int depth)[] HeadDepthOrder =
        {
            ("morda_base_back", 1),  // the layer behind the face — tre's hat
            ("morda_base", 1),       // the face itself
            ("shell", 3),
            ("eye", 6),
            ("forelock", 15),
            ("morda_overlay", 19),
            ("horn", 21),
            ("magic", 23),
            ("helm", 26),
            ("helm2", 27),
            ("konec", 29),
        };

        /// <summary>
        /// The eight armours whose 273 frame used to be undrawable as layers, and the slot each one
        /// needs. Derived from <c>swfbounds frames:273</c>: a depth-3 shell (battle, intel), a bare
        /// shape at depth 15/26/27 (sapper, tre, power, polic, spec, encl), and the depth-1 layer
        /// <c>tre</c> puts behind its face.
        /// </summary>
        static readonly (string armour, string[] slots)[] LayerArmours =
        {
            ("battle", new[] { "shell" }),
            ("intel", new[] { "shell" }),
            ("sapper", new[] { "forelock" }),
            ("tre", new[] { "morda_base_back", "helm" }),
            ("power", new[] { "helm" }),
            ("polic", new[] { "forelock", "helm2" }),
            ("spec", new[] { "forelock", "helm2" }),
            ("encl", new[] { "helm2" }),
        };

        /// <summary>
        /// Armours with no frame in 273 at all. `Obj.setArmor` throws on the missing label, catches,
        /// and shows frame 1 — so "no override" is the faithful answer and these must stay empty.
        /// </summary>
        static readonly string[] NoHeadFrame = { "pip", "chitin", "kombu", "skin", "metal" };

        CharacterAnimationDefinition _definition;
        int _mordaArmorIndex;

        [SetUp]
        public void Load()
        {
            _definition = Resources.Load<CharacterAnimationDefinition>(DefinitionPath);
            Assert.IsNotNull(_definition, $"no definition at Resources/{DefinitionPath}");

            _mordaArmorIndex = IndexOfPart("morda_armor");
        }

        int IndexOfPart(string partName)
        {
            for (int i = 0; i < _definition.parts.Length; i++)
            {
                if (_definition.parts[i].partName == partName) return i;
            }

            Assert.Fail($"the definition has no part named '{partName}'. If this is a head part, the " +
                "asset predates the head decomposition — re-import (Character Animation window) and " +
                "re-run.");
            return -1;
        }

        static ArmorPartOverride? Find(CharacterAnimationDefinition definition, string armour, int partIndex)
        {
            ArmorVisualSet set = definition.GetArmorSet(armour);
            if (set?.partOverrides == null) return null;

            foreach (ArmorPartOverride candidate in set.partOverrides)
            {
                if (candidate.partIndex == partIndex) return candidate;
            }

            return null;
        }

        [Test]
        public void HeadLayersArePlacedInDrawOrderOnEveryFrame()
        {
            // The parts array is only half of the draw order. The renderers walk
            // frameData.partPlacements and set sortingOrder = slotIdx * 2, and nothing sorts that
            // array, so the *placement* order is what decides what covers what on screen.
            //
            // This is not hypothetical: the generator collects head placements by walking frame 1 of
            // morda_armor(273), and the three slots that only an armour frame places — shell (depth
            // 3), helm2 (depth 27) and morda_base_back (depth 1) — are appended after that walk. Left
            // in insertion order they draw last, i.e. the battle helmet over the eye it is supposed
            // to sit behind, and tre's hat over its own face. The symptom looks like "the shell is
            // in the wrong place" when the layer is simply in the wrong order.
            var headPartOrder = new Dictionary<int, int>();
            for (int i = 0; i < HeadDepthOrder.Length; i++)
                headPartOrder[IndexOfPart(HeadDepthOrder[i].part)] = i;

            int framesChecked = 0;

            foreach (CharacterStateClip clip in _definition.stateClips)
            {
                for (int fi = 0; fi < clip.frames.Length; fi++)
                {
                    CharacterFrameData frame = clip.frames[fi];
                    int previousRank = -1;
                    int previousIndex = -1;

                    foreach (PartFramePlacement placement in frame.partPlacements)
                    {
                        if (!headPartOrder.TryGetValue(placement.partIndex, out int rank)) continue;

                        Assert.GreaterOrEqual(rank, previousRank,
                            $"'{clip.stateName}' frame {fi}: head part " +
                            $"'{_definition.parts[placement.partIndex].partName}' is placed after " +
                            $"'{_definition.parts[previousIndex].partName}'. The placement array is the " +
                            "draw order and it must follow the parts array; a layer collected late " +
                            "(shell, helm2, morda_base_back) lands on top of everything.");

                        previousRank = rank;
                        previousIndex = placement.partIndex;
                        framesChecked++;
                    }
                }
            }

            Assert.Greater(framesChecked, 0,
                "no head sub-part placements found in any frame — the head decomposition is not in the asset");
        }

        [Test]
        public void TheHeadBlockIsInTheOrderTheSwfDrawsIt()
        {
            // The decomposition only reproduces the original if the parts are drawn in the same
            // order. The old table was not: it put morda_overlay before eye and konec before helm,
            // so a shell at depth 3 would have landed on top of the eye it is meant to sit behind.
            for (int i = 1; i < HeadDepthOrder.Length; i++)
            {
                int previous = IndexOfPart(HeadDepthOrder[i - 1].part);
                int current = IndexOfPart(HeadDepthOrder[i].part);

                Assert.Less(previous, current,
                    $"'{HeadDepthOrder[i - 1].part}' (depth {HeadDepthOrder[i - 1].depth}) must be drawn " +
                    $"before '{HeadDepthOrder[i].part}' (depth {HeadDepthOrder[i].depth}); the parts array " +
                    "is the draw order");
            }

            Assert.Greater(_mordaArmorIndex, IndexOfPart("konec"),
                "morda_armor is the whole frame; when it is used it must draw over every layer it replaces");
        }

        [Test]
        public void NoArmourDrawsABakedHead()
        {
            // The composite exists as a fallback for a frame that cannot be taken apart. No shipped
            // armour needs it any more, and one that does is showing a frozen eye.
            foreach (string armour in _definition.armorSets.Select(s => s.armorId))
            {
                ArmorPartOverride? composite = Find(_definition, armour, _mordaArmorIndex);
                Assert.IsFalse(composite.HasValue && composite.Value.armorSprite != null,
                    $"'{armour}' draws the whole morda_armor(273) frame. Its layers are all " +
                    "addressable now (shell = depth 3, helm2 = depth 27, the rest by depth), so a " +
                    "baked frame means one of them lost its slot or its sprite — check the import log " +
                    "for \"a head layer has no sprite\".");
            }
        }

        [Test]
        public void EveryFormerlyBakedArmourDrawsItsLayers()
        {
            foreach ((string armour, string[] slots) in LayerArmours)
            {
                foreach (string slot in slots)
                {
                    int partIndex = IndexOfPart(slot);
                    ArmorPartOverride? over = Find(_definition, armour, partIndex);

                    Assert.IsTrue(over.HasValue,
                        $"'{armour}' places a layer in slot '{slot}' in its 273 frame; without the " +
                        "override that layer is not drawn at all, and the head is wrong rather than " +
                        "merely untinted");
                    Assert.IsNotNull(over.Value.armorSprite,
                        $"'{armour}' slot '{slot}' has an override with no sprite, so nothing is drawn");
                }
            }
        }

        [Test]
        public void TheEyeIsNeverDroppedFromAnArmour()
        {
            // This is the point of the whole decomposition. `eye`(220) is placed at depth 6 in all
            // sixteen frames of 273 and is never removed, so the original composes it for every
            // armour; its state, gaze and tint then come from the player (UnitPlayer.as:4690-4713,
            // Appear.trEye) instead of from the frame the exporter happened to bake. Whether the
            // armour art covers the eye is the art's business — the layer must still be drawn.
            foreach (ArmorVisualSet set in _definition.armorSets)
            {
                CollectionAssert.DoesNotContain(set.hiddenHeadParts ?? new string[0], "eye",
                    $"'{set.armorId}' hides the eye, but its morda_armor(273) frame places one at " +
                    "depth 6. A hidden eye means the frame was treated as flat, or the eye slot " +
                    "lost its placement");
            }
        }

        [Test]
        public void ThePipBuckIsDrawnOnceAndRidesTheForeleg()
        {
            // UnitPlayer.as:4828-4849 — the device is not placed by the frame at all:
            //
            //     if (vis.scaleX > 0) { pip1.visible = true;  pip2.visible = false;
            //                           pip1.x/y/rotation = frleg3.x/y/rotation; }
            //
            // morda_armor(273) places it twice (pip1 at depth 67, pip2 at depth 25) so that the
            // mirror branch has an instance on the other foreleg. Drawn as authored, both stay at
            // their frame-1 positions: over `stay` each placement's position is constant to four
            // decimals across all 76 frames while the foreleg spans 0.258 units in x, so the device
            // drifts up to 0.164 away — and a second copy sits behind the torso.
            //
            // This pins three things that are all one rule: exactly one is visible, it sits on the
            // near foreleg, and it therefore moves when the leg does.
            int pip = IndexOfPart("pip");
            int fleg3 = IndexOfPart("fleg3");
            int fleg3a = IndexOfPart("fleg3a");

            int framesChecked = 0;
            int framesWhereItMoved = 0;

            foreach (CharacterStateClip clip in _definition.stateClips)
            {
                Vector2 first = default;
                bool haveFirst = false;

                for (int fi = 0; fi < clip.frames.Length; fi++)
                {
                    PartFramePlacement[] ps = clip.frames[fi].partPlacements;

                    PartFramePlacement? visiblePip = null;
                    int visibleCount = 0;
                    foreach (PartFramePlacement p in ps)
                    {
                        if (p.partIndex != pip || !p.visible) continue;
                        visibleCount++;
                        visiblePip = p;
                    }

                    if (visibleCount == 0 && ps.All(p => p.partIndex != pip)) continue; // pose without one

                    Assert.AreEqual(1, visibleCount,
                        $"'{clip.stateName}' frame {fi} draws {visibleCount} PipBucks. The original " +
                        "shows exactly one — pip1 when facing right, pip2 when mirrored — and the " +
                        "other is only data for the branch the port does not have yet.");

                    // The near foreleg is the later of the two in draw order (Flash draws the far
                    // side first), and it is the one AS3 calls frleg3.
                    PartFramePlacement? near = null;
                    foreach (PartFramePlacement p in ps)
                    {
                        if (p.partIndex == fleg3 || p.partIndex == fleg3a) near = p;
                    }

                    if (near == null) continue;

                    float d = Vector2.Distance(visiblePip.Value.localPosition, near.Value.localPosition);
                    Assert.Less(d, 0.01f,
                        $"'{clip.stateName}' frame {fi}: the PipBuck is {d:F3} away from the near " +
                        "foreleg. It must be snapped to it (UnitPlayer.as:4835-4837 copies x, y and " +
                        "rotation), not drawn at the position the SWF authored.");

                    if (!haveFirst)
                    {
                        first = visiblePip.Value.localPosition;
                        haveFirst = true;
                    }
                    else if (Vector2.Distance(first, visiblePip.Value.localPosition) > 0.01f)
                    {
                        framesWhereItMoved++;
                    }

                    framesChecked++;
                }
            }

            Assert.Greater(framesChecked, 0, "no PipBuck placements found in any state");
            Assert.Greater(framesWhereItMoved, 0,
                "the PipBuck never moves across a whole clip, which means it is still being drawn " +
                "at its authored position rather than following the foreleg");
        }

        [Test]
        public void TheVisiblePipBuckDrawsOverTheBodyAndOverTheLegItRides()
        {
            // Draw order is the placement array, not the part table: the renderers set
            // sortingOrder = slotIdx * 2 and nothing sorts. SWFParser.SnapshotFrame fills that array
            // from a SortedDictionary keyed by depth, so the array *is* Flash's back-to-front order,
            // and this test guards the data that reaches the renderer rather than a table nothing
            // reads. Depths in `stay`(3651):
            //
            //   … flleg3@21, pip2@25, tail@27, korpus@31, neck@35, mane@43, head@47,
            //     frleg3@53, sleg1@61, pip1@67, rwing@69
            //
            // What is universal across the 881 frames that place a pip is the first half: the visible
            // device (pip1) is above the torso, above the head and above the near foreleg it is
            // strapped to. The wing is NOT a fixed neighbour — `polz`(3656) folds it at depth 55,
            // below pip1 at 70 — so it is deliberately not asserted.
            int pip = IndexOfPart("pip");
            int korpus = IndexOfPart("korpus");
            int head = IndexOfPart("head");
            int fleg3 = IndexOfPart("fleg3");
            int fleg3a = IndexOfPart("fleg3a");

            int framesChecked = 0;
            foreach (CharacterStateClip clip in _definition.stateClips)
            {
                for (int fi = 0; fi < clip.frames.Length; fi++)
                {
                    PartFramePlacement[] ps = clip.frames[fi].partPlacements;

                    int pipAt = -1, korpusAt = -1, headAt = -1, nearLegAt = -1;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        int part = ps[i].partIndex;
                        if (part == pip && ps[i].visible) pipAt = i;
                        else if (part == korpus) korpusAt = i;
                        else if (part == head) headAt = i;
                        // `stay` uses fleg3a(297) for the near foreleg, `polz` uses fleg3(124); the
                        // later of the two is the near side in both.
                        else if (part == fleg3 || part == fleg3a) nearLegAt = i;
                    }

                    if (pipAt < 0) continue;

                    Assert.Greater(pipAt, korpusAt,
                        $"'{clip.stateName}' frame {fi}: the PipBuck is placed before korpus, so the " +
                        "torso covers it. In the SWF it is at depth 63-70, above the body.");
                    Assert.Greater(pipAt, nearLegAt,
                        $"'{clip.stateName}' frame {fi}: the PipBuck is placed before the near foreleg " +
                        "it is strapped to");
                    Assert.Greater(pipAt, headAt,
                        $"'{clip.stateName}' frame {fi}: the PipBuck is placed before the head, which " +
                        "is at depth 43-47 — below the device in every body that places one");

                    framesChecked++;
                }
            }

            Assert.Greater(framesChecked, 0, "no visible PipBuck placement found in any state");
        }

        [Test]
        public void ThePipBuckShipsAtItsReserveDensity()
        {
            // A reserve is only real if the texture on disk has it, and nothing was checking that.
            // `pip`(513) shipped at 7x7 px — one texel per Flash unit, all 49 opaque, so the device
            // was a square — and every import was "correct", because CharacterSpriteImporter measures
            // whatever is on disk and derives PPU from it. The reserve is produced by a separate
            // ffdec export, and the first re-import after it was documented ran without it.
            //
            // PPU is 100 x zoom (ResolvePlacement), so the shipped density is readable straight off
            // the asset: 800 means zoom 8. Written down here rather than read from
            // CharacterArtReserve, so that lowering the table is a decision someone has to argue for.
            // Red until the art is re-exported: PFE/Art/Import Character From SWF → Run Full Import
            // exports it first (CharacterArtReserve, step 1.6).
            const int pipSymbol = 513;
            const int expectedZoom = 8;

            CharacterPartDefinition pip = _definition.parts[IndexOfPart("pip")];
            Assert.AreEqual(pipSymbol, pip.symbolId, "the part named 'pip' is not symbol 513");
            Assert.IsNotNull(pip.baseSprite, "'pip' has no base sprite");

            Assert.GreaterOrEqual(pip.baseSprite.pixelsPerUnit, 100f * expectedZoom - 0.5f,
                $"'pip'(513) ships at {pip.baseSprite.pixelsPerUnit / 100f:0.##}x " +
                $"({pip.baseSprite.rect.width:0}x{pip.baseSprite.rect.height:0} px), below its " +
                $"{expectedZoom}x reserve. At 1x the whole device is 7x7 and every texel is opaque, so " +
                "it draws as a square. Re-export it: PFE/Art/Import Character From SWF → Run Full " +
                "Import (step 1.6 runs ffdec first). docs/CharacterImportPipeline/12 §2b.");
        }

        [Test]
        public void ArmoursWithoutAFrameKeepTheUnarmouredHead()
        {
            foreach (string armour in NoHeadFrame)
            {
                Assert.IsNull(Find(_definition, armour, _mordaArmorIndex),
                    $"'{armour}' has no frame in morda_armor(273); an override here is another " +
                    "armour's head, which is exactly the pip/battle fault");
            }
        }

        [Test]
        public void TreKeepsOnlyTheLegsThatHaveATreLabel()
        {
            // sleg3a(24), sleg3(319), sleg1(55), fleg3(124) and fleg3a(297) have no 'tre' frame
            // label, so Obj.setArmor's catch leaves them at frame 1. fleg1(78) and fleg2(101) do
            // have one. An override for the other five is invented art.
            ArmorVisualSet tre = _definition.GetArmorSet("tre");
            Assert.IsNotNull(tre);

            int[] headParts = HeadDepthOrder.Select(h => IndexOfPart(h.part)).ToArray();
            int[] bodyOverrides = tre.partOverrides
                .Select(o => o.partIndex)
                .Where(i => i != _mordaArmorIndex && !headParts.Contains(i))
                .OrderBy(i => i)
                .ToArray();

            CollectionAssert.AreEquivalent(new[] { 4, 5, 11, 12 }, bodyOverrides,
                "tre's body overrides should be fleg1, fleg2, korpus and neck");
        }
    }
}
