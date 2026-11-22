using NUnit.Framework;
using PFE.Character;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Tests.EditMode.Character
{
    /// <summary>
    /// The player's body is a <i>child</i> of the symbol the game draws, and the port has to carry that
    /// placement or the whole rig comes out small.
    ///
    /// <para><b>The fault this guards.</b> AS3 builds the player as
    /// <c>vis = new visualPlayer()</c> (<c>UnitPlayer.as:334</c>) and draws the character out of
    /// <c>vis.osn</c> — <c>body</c>(3679) — which <c>visualPlayer</c>(3690) places at
    /// <b>1.2999878</b>. All 26 state clips inside 3679 are placed at identity, so that one matrix is
    /// the only thing between the art and the world. The importer read the clips and dropped the
    /// matrix, so every part was individually correct and the character drew at <b>1/1.3</b> of its
    /// AS3 height — which is what "the player looks a bit small, and smaller than the NPCs" was.</para>
    ///
    /// <para><b>Why it went unnoticed.</b> Nothing was wrong per part. The rig's own numbers (torso at
    /// 0.206, legs near 1.0) are the SWF's perspective, not a scale, so a missing outer factor looks
    /// exactly like the art. The personas never had this problem: their outer placement is stored per
    /// persona (<see cref="PFE.Data.Definitions.Campaign.NpcDefinition.visualScaleX"/>), which is why
    /// the NPCs applied their scale and the player did not.</para>
    ///
    /// <para><b>Where the rig-side half lives.</b> The transform-level contract — the body root carries
    /// <c>bodyScale</c>, the overlay mesh stays a sibling of it — is pinned renderer-side in
    /// <see cref="CharacterQuadBodyScaleTests"/>. This file pins the definition-level numbers and the
    /// one behaviour the renderer fixture cannot see: the shield dome is actually <i>on screen</i>
    /// when its gate is on.</para>
    /// </summary>
    [TestFixture]
    public class PlayerBodyPlacementTests
    {
        const string DefinitionPath = "Characters/PlayerAnimationDefinition";

        CharacterAnimationDefinition _definition;

        [SetUp]
        public void Load()
        {
            _definition = Resources.Load<CharacterAnimationDefinition>(DefinitionPath);
            Assert.IsNotNull(_definition, $"no definition at Resources/{DefinitionPath}");
        }

        [Test]
        public void ThePlayerBodyShipsAtTheScaleVisualPlayerDeclares()
        {
            // The expected value is written here, not read from the SWF or from a table, so that
            // "make the player smaller" has to be an argument against a number in a test.
            const float expectedScale = 1.2999878f;

            Assert.AreEqual(expectedScale, _definition.bodyScale.x, 1e-3f,
                $"bodyScale.x is {_definition.bodyScale.x}, not visualPlayer's osn scale " +
                $"{expectedScale}. At 1.0 the player draws at 77 % of its AS3 height — smaller than " +
                "every NPC that applies its own placement. Re-import: PFE/Art/Import Character From " +
                "SWF → Run Full Import (CharacterAnimationDataGenerator.ResolveBodyScale). " +
                "docs/CharacterImportPipeline/13.");
            Assert.AreEqual(expectedScale, _definition.bodyScale.y, 1e-3f,
                "bodyScale.y must match bodyScale.x: 3690's osn placement is uniform.");
        }

        [Test]
        public void BodyScaleFallsBackToOne_SoAnAssetSavedBeforeTheFieldExistedDoesNotCollapse()
        {
            // A field added to a ScriptableObject deserialises as 0 in every asset saved before it
            // existed — Unity does not run the field initialiser for a key that is absent from the
            // YAML. Applied raw, that zero would collapse the body root and the player would vanish
            // on the first frame after this landed, which reads as a rig bug rather than a data one.
            var definition = ScriptableObject.CreateInstance<CharacterAnimationDefinition>();
            try
            {
                definition.bodyScale = Vector2.zero;

                Assert.AreEqual(1f, definition.BodyScale.x, "a zero bodyScale.x must mean 1, not 0");
                Assert.AreEqual(1f, definition.BodyScale.y, "a zero bodyScale.y must mean 1, not 0");

                // The control: a real value must pass through untouched, so the guard cannot be
                // satisfied by a getter that always returns 1.
                definition.bodyScale = new Vector2(1.3f, 1.3f);
                Assert.AreEqual(1.3f, definition.BodyScale.x, 1e-4f);
                Assert.AreEqual(1.3f, definition.BodyScale.y, 1e-4f);
            }
            finally
            {
                Object.DestroyImmediate(definition);
            }
        }

        /// <summary>
        /// The shield dome must be <b>on screen</b> when its gate is on — not merely configured.
        /// The quad-path assertion is the overlay mesh renderer: with the consolidated meshes,
        /// "an overlay gate is on" is exactly "the overlay mesh has visible quads", which is what
        /// <see cref="CharacterQuadAssembler.IsOverlayMeshActive"/> reports.
        ///
        /// <para><b>The fault the sprite-path fixture guarded.</b> Its <c>BuildOverlays</c> created
        /// the overlay GameObject deactivated and nothing ever reactivated it, while every hide/show
        /// branch wrote the renderer's <c>enabled</c> flag. A SpriteRenderer on a deactivated
        /// GameObject draws nothing however enabled it is, so the player's <c>sp_mshit</c> dome had
        /// a correct gate (<c>shithp &gt; 0</c>), 20 imported frames, a resolved sprite and sorting
        /// order 1000, and was still invisible. The degenerate-quad design cannot have that
        /// failure mode (a hidden overlay is zeroed vertices, drawn only when the playhead lands),
        /// but "the gate says on and the mesh renderer says off" can still be written by hand —
        /// which is what this fixture pins.</para>
        ///
        /// <para><b>Both edges.</b> The gate-off half is the control: it proves the two assertions
        /// after it are reading values that can change, rather than a constant that would make the
        /// test pass against any implementation.</para>
        /// </summary>
        [Test]
        public void TheShieldDomeIsActuallyOnScreenWhenItsGateIsOn()
        {
            var go = new GameObject("player shield fixture");
            var definition = ScriptableObject.CreateInstance<CharacterAnimationDefinition>();
            var texture = new Texture2D(8, 8);
            Sprite frame = null;
            Sprite partSprite = null;
            try
            {
                frame = Sprite.Create(texture, new Rect(0f, 0f, 8f, 8f), new Vector2(0.5f, 0.5f), 1f);
                partSprite = Sprite.Create(texture, new Rect(0f, 0f, 8f, 8f), new Vector2(0.5f, 0.5f), 1f);

                definition.characterId = "fixture";
                definition.bodyScale = Vector2.one;
                definition.parts = new[]
                {
                    new CharacterPartDefinition
                    {
                        partName = "korpus",
                        baseSprite = partSprite,
                        pivotNormalized = new Vector2(0.5f, 0.5f),
                        tintCategory = TintCategory.None,
                    },
                };
                definition.stateClips = new[]
                {
                    new CharacterStateClip
                    {
                        stateName = "stay",
                        frameCount = 1,
                        loopMode = AnimationLoopMode.ClampForever,
                        frames = new[]
                        {
                            new CharacterFrameData
                            {
                                partPlacements = new[]
                                {
                                    new PartFramePlacement
                                    {
                                        partIndex = 0,
                                        localPosition = Vector2.zero,
                                        localRotation = 0f,
                                        localScale = Vector2.one,
                                        visible = true,
                                    },
                                },
                            },
                        },
                    },
                };
                definition.overlays = new[]
                {
                    new CharacterOverlayDefinition
                    {
                        overlayName = "shit",
                        frames = new[] { frame },
                        pivotNormalized = new Vector2(0.5f, 0.5f),
                        localScale = 1f,
                        sortingOrder = 1000,
                        loopMode = AnimationLoopMode.ClampForever,
                    },
                };

                var context = CharacterVisualContext.Default;
                var assembler = go.AddComponent<CharacterQuadAssembler>();
                assembler.Setup(definition, null, CharacterAppearance.CreateDefault(), context);

                // Control: gate off, so nothing may draw. The body part draws; the overlay mesh
                // must not.
                Assert.IsFalse(assembler.IsOverlayMeshActive, "the shield drew with showShield off");
                Assert.AreEqual(1, assembler.ActiveQuadCount,
                    "only the body part may be drawn while the shield gate is off");

                // The rise edge — AS3 `vis.shit.visible = true; gotoAndPlay(1)`.
                context.showShield = true;
                assembler.VisualContext = context;

                Assert.IsTrue(assembler.IsOverlayMeshActive,
                    "the shield overlay's mesh renderer is off with the gate on, so the dome is " +
                    "not on screen. This is the 'cast the shield spell and see no dome' fault in " +
                    "its quad-path shape: the gate fired but no overlay quad reached the mesh.");
                Assert.AreEqual(2, assembler.ActiveQuadCount,
                    "the body part plus exactly one shield quad should be drawn with the gate on");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(definition);
                if (frame != null) Object.DestroyImmediate(frame);
                if (partSprite != null) Object.DestroyImmediate(partSprite);
                Object.DestroyImmediate(texture);
            }
        }
    }
}
