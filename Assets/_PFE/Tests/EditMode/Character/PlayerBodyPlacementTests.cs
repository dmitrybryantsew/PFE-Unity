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
    /// <para><b>What is pinned and what is not.</b> The scale, the fact that a built rig carries it on
    /// a transform of its own, and the fact that the overlays are <i>not</i> under that transform —
    /// because in the SWF they are siblings of <c>osn</c>, so scaling the body must not scale the
    /// shield dome. The exact node names are not pinned beyond the overlay prefix the assembler
    /// already used.</para>
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

        [Test]
        public void TheBodyRootCarriesTheScaleAndTheOverlaysStayBesideIt()
        {
            // Storing the scale is only half of it: the fault being fixed was a value nothing read.
            // The other half is *where* it lands. In the SWF, vis.shit(3625) and friends are siblings
            // of osn inside visualPlayer, so scaling the body must not scale the shield dome — a dome
            // 30 % too big is a bug nobody would trace back to the player's height.
            var go = new GameObject("player rig fixture");
            var definition = ScriptableObject.CreateInstance<CharacterAnimationDefinition>();
            try
            {
                definition.characterId = "fixture";
                definition.bodyScale = new Vector2(1.3f, 1.3f);
                definition.parts = System.Array.Empty<CharacterPartDefinition>();
                definition.stateClips = System.Array.Empty<CharacterStateClip>();
                definition.overlays = new[]
                {
                    new CharacterOverlayDefinition { overlayName = "shit", sortingOrder = 1000 },
                };

                var assembler = go.AddComponent<CharacterSpriteAssembler>();
                assembler.Setup(definition, null, CharacterAppearance.CreateDefault());

                Assert.IsNotNull(assembler.BodyRoot,
                    "a built rig has no body root, so the part slots hang straight off the assembler " +
                    "and nothing can carry the body's placement scale");
                Assert.AreSame(go.transform, assembler.BodyRoot.parent,
                    "the body root must be a child of the character, not the character itself");
                Assert.AreEqual(1.3f, assembler.BodyRoot.localScale.x, 1e-4f,
                    "the body root did not take the definition's bodyScale");
                Assert.AreEqual(1.3f, assembler.BodyRoot.localScale.y, 1e-4f,
                    "the body root did not take the definition's bodyScale");

                Transform overlay = go.transform.Find("overlay_shit");
                Assert.IsNotNull(overlay, "the shield overlay was not built");
                Assert.AreSame(go.transform, overlay.parent,
                    "the overlay is a sibling of the body in the SWF; parenting it under the body " +
                    "root would scale the shield dome with the character");
                Assert.AreEqual(1f, overlay.localScale.x, 1e-4f,
                    "the overlay picked up the body's scale");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(definition);
            }
        }

        /// <summary>
        /// The shield dome must be <b>on screen</b> when its gate is on — not merely configured.
        ///
        /// <para><b>The fault this guards.</b> <c>BuildOverlays</c> created the overlay GameObject
        /// deactivated and nothing ever reactivated it, while every hide/show branch in
        /// <c>SyncOverlays</c> writes the renderer's <c>enabled</c> flag. A SpriteRenderer on a
        /// deactivated GameObject draws nothing however enabled it is, so the player's
        /// <c>sp_mshit</c> dome had a correct gate (<c>shithp &gt; 0</c>), 20 imported frames, a
        /// resolved sprite and sorting order 1000, and was still invisible. Every one of those was
        /// asserted somewhere; the one thing nobody asserted was that the object is alive.</para>
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
            try
            {
                frame = Sprite.Create(texture, new Rect(0f, 0f, 8f, 8f), new Vector2(0.5f, 0.5f), 1f);

                definition.characterId = "fixture";
                definition.bodyScale = Vector2.one;
                definition.parts = System.Array.Empty<CharacterPartDefinition>();
                definition.stateClips = System.Array.Empty<CharacterStateClip>();
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
                var assembler = go.AddComponent<CharacterSpriteAssembler>();
                assembler.Setup(definition, null, CharacterAppearance.CreateDefault(), context);

                Transform overlay = go.transform.Find("overlay_shit");
                Assert.IsNotNull(overlay, "the shield overlay was not built");
                var renderer = overlay.GetComponent<SpriteRenderer>();
                Assert.IsNotNull(renderer, "the shield overlay has no SpriteRenderer");

                // Control: gate off, so nothing may draw.
                Assert.IsFalse(renderer.enabled, "the shield drew with showShield off");

                // The rise edge — AS3 `vis.shit.visible = true; gotoAndPlay(1)`.
                context.showShield = true;
                assembler.VisualContext = context;

                Assert.IsTrue(overlay.gameObject.activeSelf,
                    "the shield overlay's GameObject is deactivated, so its SpriteRenderer draws " +
                    "nothing no matter what `enabled` says. This is the 'cast the shield spell and " +
                    "see no dome' fault: BuildOverlays switched the object off and nothing ever " +
                    "switched it back on.");
                Assert.IsTrue(renderer.enabled, "the shield renderer was not enabled on the rise edge");
                Assert.IsNotNull(renderer.sprite, "the shield overlay resolved no sprite");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(definition);
                if (frame != null) Object.DestroyImmediate(frame);
                Object.DestroyImmediate(texture);
            }
        }
    }
}
