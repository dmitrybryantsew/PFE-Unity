using NUnit.Framework;
using PFE.Character;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Tests.EditMode.Character
{
    /// <summary>
    /// The quad renderer must carry the same <c>osn</c> scale the original SWF tree does.
    ///
    /// <para><b>The fault this guards.</b> The character's part quads hang from a <c>body</c> node
    /// whose <c>localScale</c> is <see cref="CharacterAnimationDefinition.BodyScale"/>, which for
    /// the player is <b>1.2999878</b>. An earlier quad path baked the placement into four vertices
    /// and read <c>BodyScale</c> <i>nowhere</i> — so it drew the character at <b>1/1.3 = 0.769</b>
    /// of the sprite path: every part too small, and every part too close to the origin. That is
    /// the quad path's version of the fault <see cref="PlayerBodyPlacementTests"/> pinned at the
    /// definition level — a value that existed and nothing read.</para>
    ///
    /// <para><b>Why a geometry fixture and not a scale assertion.</b> "The body root has scale 1.3"
    /// can pass while the quads ignore it, because the scale could be set on a node the meshes are
    /// not under. So the fixture hands the renderer a known definition, a known placement and a
    /// known sprite, and asserts where it <i>actually puts the part</i> — a world centre and a
    /// world size written down from the fixture's own numbers (1 unit square at (0.5, 0.25),
    /// scaled by 1.3), not read back from the renderer.</para>
    /// </summary>
    [TestFixture]
    public class CharacterQuadBodyScaleTests
    {
        const float BodyScale = 1.3f;

        // The fixture's sprite is 100 x 100 px at 100 px/unit, i.e. exactly 1 unit square with its
        // registration point in the middle. Every expected number below is derived from those two
        // facts and the placement, not read back from either renderer.
        const int SpritePixels = 100;
        const float PixelsPerUnit = 100f;

        static readonly Vector2 PlacementPosition = new(0.5f, 0.25f);

        // 1 unit square, placed at (0.5, 0.25), scaled by the body: size 1.3, centre (0.65, 0.325).
        static readonly Vector2 ExpectedSize = new(BodyScale, BodyScale);
        static readonly Vector2 ExpectedCentre =
            new(PlacementPosition.x * BodyScale, PlacementPosition.y * BodyScale);

        GameObject _go;
        Texture2D _texture;
        Sprite _sprite;
        CharacterAnimationDefinition _definition;

        [SetUp]
        public void Build()
        {
            _texture = new Texture2D(SpritePixels, SpritePixels, TextureFormat.RGBA32, false);
            var pixels = new Color32[SpritePixels * SpritePixels];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(255, 255, 255, 255);
            }
            _texture.SetPixels32(pixels);
            _texture.Apply();

            _sprite = Sprite.Create(
                _texture,
                new Rect(0f, 0f, SpritePixels, SpritePixels),
                new Vector2(0.5f, 0.5f),
                PixelsPerUnit);

            _definition = ScriptableObject.CreateInstance<CharacterAnimationDefinition>();
            _definition.characterId = "fixture";
            _definition.bodyScale = new Vector2(BodyScale, BodyScale);
            _definition.parts = new[]
            {
                new CharacterPartDefinition
                {
                    partName = "korpus",
                    baseSprite = _sprite,
                    pivotNormalized = new Vector2(0.5f, 0.5f),
                    tintCategory = TintCategory.None
                }
            };
            _definition.stateClips = new[]
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
                                    localPosition = PlacementPosition,
                                    localRotation = 0f,
                                    localScale = Vector2.one,
                                    visible = true
                                }
                            }
                        }
                    }
                }
            };
            _definition.overlays = new[]
            {
                new CharacterOverlayDefinition { overlayName = "shit", sortingOrder = 1000 }
            };

            _go = new GameObject("quad body scale fixture");
        }

        [TearDown]
        public void Destroy()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_definition != null) Object.DestroyImmediate(_definition);
            if (_sprite != null) Object.DestroyImmediate(_sprite);
            if (_texture != null) Object.DestroyImmediate(_texture);
        }

        CharacterQuadAssembler QuadAssembler()
        {
            var assembler = _go.AddComponent<CharacterQuadAssembler>();
            assembler.Setup(_definition, null, CharacterAppearance.CreateDefault());
            return assembler;
        }

        [Test]
        public void TheQuadSidePutsAPartWhereTheFixtureSaysItBelongs()
        {
            // The invariant, against numbers written down from the fixture itself: a 1×1 unit quad
            // at (0.5, 0.25), carried through a 1.3 body scale, lands at centre (0.65, 0.325) with
            // size 1.3. A failure reads directly as "the body scale is not being applied".
            CharacterQuadAssembler quadAssembler = QuadAssembler();

            CharacterQuadAssembler.QuadSlot quadSlot = quadAssembler.GetQuadSlot("korpus");
            Assert.IsNotNull(quadSlot, "the quad side drew no korpus");
            Assert.IsTrue(quadSlot.visible, "the quad slot was not written into the mesh");

            GetQuadBounds(quadSlot, out Vector3 quadCentre, out Vector3 quadSize);

            Assert.AreEqual(ExpectedCentre.x, quadCentre.x, 1e-3f,
                $"the quad side put the part at x {quadCentre.x}, expected {ExpectedCentre.x}. At " +
                $"bodyScale {BodyScale} a renderer that ignores BodyScale draws at " +
                $"{1f / BodyScale:0.####} of the fixture size — which is what the quad path did " +
                "before it grew a body root.");
            Assert.AreEqual(ExpectedCentre.y, quadCentre.y, 1e-3f,
                "the quad side's vertical placement disagrees with the fixture");

            Assert.AreEqual(ExpectedSize.x, quadSize.x, 1e-3f,
                $"the quad side drew the part {quadSize.x} wide, expected {ExpectedSize.x}. A part's " +
                "world size is rect / ppu times every scale between it and the camera, and bodyScale " +
                "is one of them.");
            Assert.AreEqual(ExpectedSize.y, quadSize.y, 1e-3f,
                "the quad side's height disagrees with the fixture");
        }

        [Test]
        public void TheQuadBodyRootCarriesTheScaleAndTheOverlaysStayBesideIt()
        {
            // The node itself, so a failure reads as "nothing carries the scale" rather than as a
            // 30 % size difference to be reverse-engineered. The overlay half is the same rule the
            // original holds: the shield dome is a sibling of osn in the SWF, so scaling the body
            // must not scale it — a dome 30 % too big is a bug nobody would trace back to the
            // player's height.
            CharacterQuadAssembler assembler = QuadAssembler();

            Assert.IsNotNull(assembler.BodyRoot,
                "a built quad rig has no body root, so the part quads hang straight off the " +
                "assembler and nothing carries the body's placement scale");
            Assert.AreSame(_go.transform, assembler.BodyRoot.parent,
                "the body root must be a child of the character, not the character itself");
            Assert.AreEqual(BodyScale, assembler.BodyRoot.localScale.x, 1e-4f,
                "the quad body root did not take the definition's bodyScale");
            Assert.AreEqual(BodyScale, assembler.BodyRoot.localScale.y, 1e-4f);

            CharacterQuadAssembler.QuadSlot slot = assembler.GetQuadSlot("korpus");
            Assert.IsNotNull(slot, "the quad side drew no korpus");
            Assert.IsTrue(slot.visible, "the quad slot was not written into the shared mesh");

            // With the consolidated mesh, all part quads share a single mesh renderer under the
            // body root. The assertion is about the renderer's parent, not a per-slot GameObject.
            Transform meshRoot = assembler.BodyRoot.Find("quad_mesh");
            Assert.IsNotNull(meshRoot, "the consolidated mesh root was not built under the body root");
            Assert.AreSame(assembler.BodyRoot, meshRoot.parent,
                "the mesh root is not under the body root, so the shared mesh is not scaled " +
                "by the body's placement and the whole character draws at 1/bodyScale");

            // The overlays answer the same question from the other side: their mesh hangs off the
            // assembler root, NOT the body root — in the SWF vis.shit(3625) and its siblings are
            // children of visualPlayer, beside osn, at their own scales.
            Transform overlayMeshRoot = _go.transform.Find("quad_overlay_mesh");
            Assert.IsNotNull(overlayMeshRoot, "the overlay mesh root was not built");
            Assert.AreSame(_go.transform, overlayMeshRoot.parent,
                "the overlay mesh must be a sibling of the body root; parenting it under the body " +
                "root would scale the shield dome with the character");
            Assert.AreEqual(1f, overlayMeshRoot.localScale.x, 1e-4f,
                "the overlay mesh picked up the body's scale");
            Assert.AreEqual(1f, overlayMeshRoot.localScale.y, 1e-4f,
                "the overlay mesh picked up the body's scale");
        }

        [Test]
        public void ARebuildPicksUpAChangedBodyScaleInsteadOfKeepingTheOldNode()
        {
            // The scale is re-read on every build, not cached on the node: an import that changes the
            // definition must not leave a stale transform behind on a survivor. Same rule and same
            // reason as EnsureBodyRoot's: a value that exists but nothing reads is the fault this
            // node was added to fix.
            CharacterQuadAssembler assembler = QuadAssembler();
            Assert.AreEqual(BodyScale, assembler.BodyRoot.localScale.x, 1e-4f);

            _definition.bodyScale = new Vector2(2f, 2f);
            assembler.Rebuild();

            Assert.AreEqual(2f, assembler.BodyRoot.localScale.x, 1e-4f,
                "the rebuild kept the body root it was born with, so a re-import cannot change the " +
                "player's height without a domain reload");
        }

        /// <summary>
        /// The quad's world-space box, read from the shared mesh's vertices rather than from
        /// <c>renderer.bounds</c>, so the assertion is about the geometry the renderer writes and not
        /// about Unity's cached bounds. With the consolidated mesh, the slot's vertices are in the
        /// shared mesh's vertex buffer at <c>vertexBase</c>, and the transform is the mesh root's.
        /// </summary>
        static void GetQuadBounds(
            CharacterQuadAssembler.QuadSlot slot, out Vector3 centre, out Vector3 size)
        {
            // The slot's renderer shim returns the shared mesh renderer, whose transform is the
            // mesh root under the body root.
            Transform transform = slot.renderer.transform;
            Mesh sharedMesh = slot.renderer.GetComponent<MeshFilter>().sharedMesh;
            Vector3[] vertices = sharedMesh.vertices;

            int vb = slot.vertexBase;
            Vector3 min = transform.TransformPoint(vertices[vb]);
            Vector3 max = min;

            for (int i = 1; i < CharacterQuadGeometry.VertexCount; i++)
            {
                Vector3 world = transform.TransformPoint(vertices[vb + i]);
                min = Vector3.Min(min, world);
                max = Vector3.Max(max, world);
            }

            centre = (min + max) * 0.5f;
            size = max - min;
        }
    }
}
