using System.Collections.Generic;
using PFE.Core.Pooling;
using PFE.Systems.Map;
// For MapSortingLayers. Same assembly (PFE.Core.asmdef), so this is a namespace reference and not a
// dependency — and it is what stops the layer name being a free-floating string, which is how it was
// wrong for as long as it was (see DefaultSortingLayer).
using PFE.Systems.Map.Rendering;
using UnityEngine;

namespace PFE.Systems.Particles.Rendering
{
    /// <summary>
    /// Draws the live particle population — one pooled <see cref="SpriteRenderer"/> per live part, reading
    /// <see cref="ParticleWorld.Live"/> every frame.
    ///
    /// <para><b>Where this sits in the frame.</b> <see cref="LateUpdate"/>, deliberately, and it is a view
    /// pass rather than a tick: the authoritative step happened on <see cref="SimLoop"/> at
    /// <c>SimTickOrder.PreTick</c>, and this only reads the result. Nothing here may write sim state, and
    /// nothing here may read <c>Time.deltaTime</c> — a frame may contain zero ticks or five, and the
    /// population already knows where it is.</para>
    ///
    /// <para><b>Ordering: the sort key is <c>sloy</c>, and every shipped row uses band 3.</b> AS3 parents
    /// each <c>Part</c> into <c>Grafon.visObjs[sloy]</c> (<c>Pt.as:40</c>), and within a band the order is
    /// <c>addChild</c> insertion order. The port reproduces both with one integer:
    /// <c>sortingOrder = bandBase + sloy * bandStride + rank</c>, where <c>rank</c> counts earlier live
    /// parts in the same band this frame. Because <see cref="ParticleWorld.Step"/> preserves spawn order
    /// and <c>Emit</c> appends, walking <see cref="ParticleWorld.Live"/> front-to-back and counting is
    /// exactly insertion order. <c>bandStride</c> must exceed the global live ceiling
    /// (<see cref="ParticleBudget.DefaultMaxParts"/> = 100) or two bands would interleave.</para>
    ///
    /// <para><b>The one number that is not derivable, and why it is serialized.</b> In AS3 the six bands
    /// are <i>interleaved with the room's own layers</i>, not stacked above them
    /// (<c>Grafon.as:193-202</c>): <c>visBack</c>, <c>visBack2</c>, bands 0-2, <c>visFront</c> (the front
    /// wall-tile bitmap), band 3, <c>visVoda</c>, <c>visLight</c>, band 4, <c>visSats</c>, band 5. So bands
    /// 0/1/2 draw <b>behind the walls</b> and 3/4/5 <b>in front</b>, and the concrete requirement is
    /// "above the foreground tiles, below water and lighting". The port has no constant for the front-wall
    /// layer or the lighting overlay — <c>RoomBackdropRenderer</c> gives <c>-5000</c> (backdrop),
    /// <c>-1000</c> (decoration), <c>1000</c> (tile shadow) and <c>5000</c> (visibility mask), with the
    /// tile grid and units both keyed on <c>-y</c>. The right value between those is a visual judgement, so
    /// it is a serialized field rather than a hard-coded number that needs a code change per attempt.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ParticleRenderer : MonoBehaviour
    {
        /// <summary>AS3 <c>Grafon.kolObjs</c> — six <c>visObjs</c> bands (<c>Grafon.as:184-190</c>).</summary>
        public const int BandCount = 6;

        /// <summary>Ceiling on pooled sprites. Above the oracle's global live ceiling of 100, with slack.</summary>
        private const int MaxPooledSprites = 256;

        /// <summary>
        /// The sorting layer every particle sprite goes on.
        /// </summary>
        /// <remarks>
        /// <para><b>This was <c>"Default"</c>, and that one word put every particle behind the whole
        /// room.</b> <c>Default</c> is the built-in layer and it is the <b>first</b> entry in the
        /// project's sorting-layer list, so it renders <i>behind</i> every custom layer — behind
        /// <c>Backwall</c>, the tile grid, the props, the units and the fog mask. The band arithmetic
        /// below was then meaningless: <c>sortingOrder</c> only breaks ties <i>inside</i> a layer, so
        /// 2000 on <c>Default</c> loses to 0 on <c>MainTiles</c>. The symptom is a particle that is
        /// visible only where nothing covers it — which reads as "blood works, but only at the edge of
        /// the map".</para>
        ///
        /// <para><b>Why <c>Foreground</c> specifically, and it is forced rather than preferred.</b> The
        /// rule is plan §12.2's, read off the oracle: <c>visObjs[3]</c> is added to the room's layer
        /// stack <i>after</i> <c>visFront</c> (the front wall tiles) and <i>before</i> <c>visVoda</c>
        /// (water) and <c>visLight</c>. In this port both the front tiles
        /// (<c>TileRenderer.frontSortingLayerName</c>) and the fog/visibility mask
        /// (<c>RoomBackdropRenderer.VisibilityMaskSortingOrder = 5000</c>) are on <c>Foreground</c>, so
        /// "above the front tiles and below the lighting overlay" can only mean <b><c>Foreground</c> at
        /// an order between the two</b> — a lower layer would be below the front tiles, and a higher one
        /// would be above the fog and would light up rooms the player has never entered. Within
        /// <c>Foreground</c> the order bands are: front tiles <c>≈0..2</c>, debug markers <c>998/999</c>,
        /// character slots and overlays <c>0..~1000</c>, the fog mask <c>5000</c>, the collider overlay
        /// <c>32000</c> — so <c>2000..3379</c> (see <see cref="DefaultBandStride"/>) is the free slot, and
        /// it also puts particles in front of characters, which is what AS3 does (particles sloy 3,
        /// units sloy 2).</para>
        ///
        /// <para><b>One recorded divergence: water.</b> AS3 puts <c>visVoda</c> after
        /// <c>visObjs[3]</c>, so submerged blood is tinted by the water. Here <c>Water</c> is a
        /// <i>lower</i> layer than <c>Foreground</c> — the port already draws the player's body above
        /// water — so blood draws over water rather than under it. The alternative (dropping below
        /// <c>Water</c>) would break "above the front tiles", which is the rule that matters for
        /// visibility.</para>
        /// </remarks>
        public const string DefaultSortingLayer = MapSortingLayers.Foreground;

        /// <summary>
        /// The sorting order for <c>sloy</c> 0. Chosen so the whole band range
        /// (<see cref="DefaultBandBaseSortingOrder"/> .. base + 5 × <see cref="DefaultBandStride"/> +
        /// <c>MaxParts</c>) sits above every character and below the fog mask — see
        /// <see cref="DefaultSortingLayer"/> for the band census.
        /// </summary>
        public const int DefaultBandBaseSortingOrder = 2000;

        /// <summary>
        /// The gap between <c>sloy</c> bands. Must exceed the global live-particle ceiling
        /// (<c>ParticleBudget.DefaultMaxParts</c> = 100) so two bands can never interleave.
        /// </summary>
        public const int DefaultBandStride = 256;

        [Header("Sorting")]
        [Tooltip("Sorting layer for every particle sprite. Must be a room layer — see DefaultSortingLayer " +
                 "for why Foreground, and why Default is never right here.")]
        [SerializeField] private string sortingLayerName = DefaultSortingLayer;

        [Tooltip("Sorting order for sloy 0. Bands 0/1/2 draw behind the front wall tiles and 3/4/5 in " +
                 "front of them, so this must land above the tile grid and units and below the water, " +
                 "lighting and fog overlays. Every shipped row is sloy 3.")]
        [SerializeField] private int bandBaseSortingOrder = DefaultBandBaseSortingOrder;

        [Tooltip("Gap between sloy bands. Must exceed the global live-particle ceiling (100) so two " +
                 "bands can never interleave.")]
        [SerializeField] private int bandStride = DefaultBandStride;

        private ParticleWorld _world;
        private ParticleSpriteCatalog _catalog;
        private GameObjectPool<SpriteRenderer> _pool;
        private GameObject _template;

        private readonly List<SpriteRenderer> _active = new List<SpriteRenderer>();
        private readonly int[] _bandRank = new int[BandCount];

        /// <summary>Guards <see cref="ValidateSortingLayer"/> so the report is made once, not per frame.</summary>
        private bool _sortingLayerValidated;

        /// <summary>
        /// The room transform the parts are parented to. <b>Null means "do not draw"</b>, and that is
        /// deliberate: <c>ParticleState.X/Y</c> are room-local pixels and
        /// <see cref="WorldCoordinates.PixelToUnity"/> applies no room offset, so a renderer parented at
        /// the world origin would place every particle correctly <i>only in the origin room</i> and
        /// displaced by a whole room width everywhere else. Refusing to draw is the honest failure; the
        /// alternative is a bug that looks perfect in the room everyone tests in.
        /// </summary>
        private Transform _roomRoot;

        /// <summary>
        /// The room's height in tiles, used to mirror <c>ParticleState.Y</c> from AS3's downward
        /// pixel axis into the port's upward one.
        ///
        /// <para><b>Without this flip every particle draws mirrored about the room's mid-height</b>, and
        /// the failure is quiet: a muzzle flash appears below the barrel, an explosion's sparks drift
        /// upward, and a burst that is symmetric looks fine. <c>ParticleState.Y</c> is AS3 space
        /// (<see cref="ParticleRules"/> integrates AS3's <c>Y += ddy</c>, which is downward), while the
        /// port's room-local Unity space is upward with row 0 at the floor. See
        /// <see cref="WorldCoordinates.As3YToUnityLocalY"/> for the shared mirror.</para>
        /// </summary>
        private int _roomHeightTiles = WorldConstants.ROOM_HEIGHT;

        /// <summary>True once a room has attached and the container has supplied the world and catalogue.</summary>
        public bool IsDrawing => _roomRoot != null && _world != null;

        /// <summary>Sprites currently borrowed from the pool — the readback for "the pool is leaking".</summary>
        public int DrawnCount => _active.Count;

        /// <summary>
        /// Wires the two collaborators. Called once by whoever creates this component; safe to call again
        /// with a new world (a session restart) because the pool is independent of both.
        /// </summary>
        public void Initialize(ParticleWorld world, ParticleSpriteCatalog catalog)
        {
            _world = world;
            _catalog = catalog;
            ValidateSortingLayer();
        }

        /// <summary>
        /// Reports a sorting layer that would put every particle behind the room.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this exists rather than a fixture.</b> The bug this renderer shipped with was a
        /// wrong <i>layer name</i>, and no offline test can catch that: the layer's meaning is its
        /// <b>position in <c>ProjectSettings/TagManager.asset</c></b>, not anything in C#, and a fixture
        /// that asserted the constant equals the constant it is defined as would be a tautology. Unity
        /// does not help either — an unrecognised name resolves to layer id <c>0</c>, which is
        /// <c>Default</c>, which is the bottom of the stack. So the misconfiguration has no failure mode
        /// of its own; the only symptom is "particles are invisible behind the map", which is
        /// indistinguishable from "no particles were emitted". This converts it into a named error at the
        /// one moment the layer is known.</para>
        ///
        /// <para><b>The consequence is spelled out in the message on purpose.</b> "Not a valid sorting
        /// layer" invites a shrug; "every particle will draw behind the map" is the thing that was
        /// actually wrong and the thing worth recognising next time.</para>
        /// </remarks>
        private void ValidateSortingLayer()
        {
            if (_sortingLayerValidated) return;
            _sortingLayerValidated = true;

            if (string.IsNullOrEmpty(sortingLayerName))
            {
                Debug.LogError(
                    "[ParticleRenderer] No sorting layer set. Unity falls back to `Default`, which is " +
                    "the first layer in the project's list and renders behind every room layer — so " +
                    $"every particle will draw behind the map. Set it to `{DefaultSortingLayer}`.");
                return;
            }

            // `Default` is a real layer, so it must be rejected by name rather than by lookup: both it
            // and an unknown name answer id 0.
            if (sortingLayerName == "Default")
            {
                Debug.LogError(
                    $"[ParticleRenderer] sortingLayerName is `Default`, which renders behind every room " +
                    $"layer (Backwall, MainTiles, the units, the fog mask) — so every particle will draw " +
                    $"behind the map and be visible only where nothing covers it. Set it to " +
                    $"`{DefaultSortingLayer}`.");
                return;
            }

            if (SortingLayer.NameToID(sortingLayerName) == 0)
            {
                Debug.LogError(
                    $"[ParticleRenderer] sortingLayerName `{sortingLayerName}` is not a registered " +
                    "sorting layer, so Unity resolves it to `Default` — every particle will draw behind " +
                    "the map. Add the layer in Project Settings > Tags and Layers, or set it to " +
                    $"`{DefaultSortingLayer}`.");
            }
        }

        /// <summary>
        /// Parents the parts to a room. AS3 adds each <c>Part</c> to the <b><c>Location</c></b>
        /// (<c>Emitter.as:362</c>), so particles are room-scoped and die with the room — a port that
        /// parented them to the unit would give the player muzzle smoke that follows them around.
        /// </summary>
        /// <param name="roomRoot">The room root. Null is ignored (the view stays detached).</param>
        /// <param name="roomHeightTiles">
        /// The room's height in tiles, for the AS3→port Y mirror. Taken as an argument rather than read
        /// from <see cref="WorldConstants.ROOM_HEIGHT"/> because rooms may be shorter than the default
        /// and a wrong height mirrors every particle by half the difference.
        /// </param>
        public void AttachTo(Transform roomRoot, int roomHeightTiles)
        {
            if (roomRoot == null) return;

            _roomRoot = roomRoot;
            _roomHeightTiles = roomHeightTiles > 0 ? roomHeightTiles : WorldConstants.ROOM_HEIGHT;
            transform.SetParent(roomRoot, false);
            transform.localPosition = Vector3.zero;
        }

        /// <summary>
        /// Detaches and releases every drawn sprite — the room-teardown half of
        /// <see cref="AttachTo"/>. Safe to call when nothing is attached.
        /// </summary>
        public void Detach()
        {
            ReleaseAll();
            _roomRoot = null;
            transform.SetParent(null, false);
        }

        private void LateUpdate()
        {
            if (!IsDrawing) return;
            Draw();
        }

        private void OnDestroy()
        {
            _pool?.Clear();
            if (_template != null) Destroy(_template);
        }

        private void Draw()
        {
            IReadOnlyList<ParticleState> live = _world.Live;
            ParticleDefinitionTable definitions = _world.Definitions;

            for (int i = 0; i < _bandRank.Length; i++) _bandRank[i] = 0;

            // `_active[0 .. used)` are the renderers in use this frame, in draw order. The tail is
            // released below. A part whose art is missing is skipped without consuming a renderer —
            // which is why the tail, not the live count, decides how many to give back.
            int used = 0;

            for (int i = 0; i < live.Count; i++)
            {
                ParticleState state = live[i];

                ParticleDefinition definition = definitions[state.DefinitionIndex];
                Sprite sprite = definition == null ? null : _catalog?.FrameAt(definition, state.DrawFrame);

                // An unrun importer or a genuinely missing asset lands here. Nothing is drawn, and
                // `ParticleSpriteCatalog.MissingIds` is where the miss is reported.
                if (sprite == null) continue;

                if (used == _active.Count) _active.Add(GetPooledRenderer());

                int band = state.Sloy >= 0 && state.Sloy < BandCount ? state.Sloy : BandCount - 1;
                int rank = _bandRank[band]++;

                Apply(_active[used], state, sprite, band, rank);
                used++;
            }

            // Hand the surplus back. Without this a part that dies mid-frame keeps its renderer enabled
            // with last frame's sprite — a ghost particle that never goes away.
            while (_active.Count > used)
            {
                SpriteRenderer renderer = _active[_active.Count - 1];
                _active.RemoveAt(_active.Count - 1);
                _pool.Release(renderer);
            }
        }

        private SpriteRenderer GetPooledRenderer()
        {
            EnsurePool();
            return _pool.Get();
        }

        private void Apply(SpriteRenderer renderer, in ParticleState state, Sprite sprite, int band, int rank)
        {
            renderer.gameObject.SetActive(true);
            renderer.sprite = sprite;
            renderer.enabled = state.Visible;
            renderer.sortingLayerName = sortingLayerName;
            renderer.sortingOrder = bandBaseSortingOrder + band * bandStride + rank;

            // Room-local pixels, no room offset applied here — the room parent owns that. Y is mirrored
            // out of AS3's downward axis first (see _roomHeightTiles): an unmirrored Y still looks
            // plausible in a symmetric burst and is wrong everywhere else.
            renderer.transform.localPosition =
                WorldCoordinates.PixelToUnity(
                    new Vector2(state.X, WorldCoordinates.As3YToUnityLocalY(state.Y, _roomHeightTiles)));

            // AS3 rotation is clockwise-positive; Unity's Z rotation is counter-clockwise-positive.
            renderer.transform.localRotation = Quaternion.Euler(0f, 0f, -state.R);

            // `mirr` negates scaleX in the oracle, and it is applied last, after every other scale term
            // (Emitter.as:354-357).
            float scaleX = state.Mirr ? -state.Scale : state.Scale;
            renderer.transform.localScale = new Vector3(scaleX, state.Scale, 1f);

            // `prealph` rows spawn at alpha 0 and fade in; the rules already resolved which of the three
            // alpha branches applies, so this only copies the result.
            renderer.color = new Color(1f, 1f, 1f, state.Alpha);
        }

        private void ReleaseSurplus(int needed)
        {
            while (_active.Count > needed)
            {
                SpriteRenderer renderer = _active[_active.Count - 1];
                _active.RemoveAt(_active.Count - 1);
                _pool.Release(renderer);
            }
        }

        private void ReleaseAll() => ReleaseSurplus(0);

        /// <summary>
        /// Builds the pool on first use. The template is a hidden, inactive, root-level GameObject so it
        /// never appears in the room hierarchy and never renders — <see cref="GameObjectPool{T}"/> clones
        /// it per instance.
        /// </summary>
        private void EnsurePool()
        {
            if (_pool != null) return;

            _template = new GameObject("__ParticleSpriteTemplate")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            _template.SetActive(false);
            _template.AddComponent<SpriteRenderer>();

            _pool = new GameObjectPool<SpriteRenderer>(
                _template.GetComponent<SpriteRenderer>(),
                initialSize: 0,
                maxSize: MaxPooledSprites,
                parent: transform);
        }
    }
}
