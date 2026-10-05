#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using PFE.Editor.Importers;
using Unity.VectorGraphics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PFE.Editor.VectorSample
{
    /// <summary>
    /// A standalone harness for the AS3 vector export. Imports one of the 2563 exported shape SVGs,
    /// tessellates it, renders it, and puts it next to the raster the port actually has — so
    /// "should we use the vector art?" becomes a measurement instead of an opinion.
    ///
    /// <para><b>Nothing here touches gameplay.</b> No scene asset, no prefab, no imported texture, no
    /// runtime type is read or written. The preview lives in an off-screen scene built from
    /// <c>HideFlags.HideAndDontSave</c> objects at a far world offset, and is destroyed on close — the
    /// same isolation <see cref="PFE.Editor.Importers.SWF.CharacterAnimationPreviewWindow"/> uses.</para>
    ///
    /// <para><b>What it can settle:</b> whether a <c>.svg</c> imports at all in this project (there are
    /// currently <b>zero</b> <c>.svg</c> files under <c>Assets/</c>, so the ScriptedImporter has never
    /// fired here), whether the built-in vector shaders render under this URP 2D setup or our own
    /// <c>SRPDefaultUnlit</c> shaders are needed, how gradients and bitmap fills survive, and — the
    /// actual question — how the tessellated result compares with a 1-pixel-per-unit raster at the same
    /// on-screen size.</para>
    ///
    /// <para><b>What it cannot settle:</b> per-animation composition. The export is one SVG per
    /// <c>DefineShape</c>, and there is no composed example anywhere on disk — <c>_assets/frames/</c>
    /// holds a single <c>1.png</c>, and no file in the tree uses <c>&lt;use&gt;</c>. A character is 26
    /// parts re-assembled per frame, and that work is invisible from here.</para>
    /// </summary>
    public class VectorSampleWindow : EditorWindow
    {
        // ── Constants ────────────────────────────────────────────────────────

        /// <summary>Matches <c>CharacterSpriteImporter.PixelsPerUnit</c>, so "1x" here means the same
        /// thing it means to the imported PNGs.</summary>
        const float SvgPixelsPerUnit = 100f;

        /// <summary>Layer 31, same as the other preview window. Isolation between the two windows comes
        /// from the world offset below, not from the layer — an orthographic camera of size 0.5..8 at
        /// x=1000 cannot see objects at x=500000, and vice versa.</summary>
        const int PreviewLayer = 31;

        /// <summary>Far enough that no other preview camera can frame it. Load-bearing: do not shrink.</summary>
        const float PreviewOffsetX = 500000f;

        const string ExportRoot = "Assets/_PFE/Art/VectorSamples";
        const float RowHeight = 18f;

        // Zoom bounds, measured rather than guessed. Across all 2563 exported shapes (2026-10-05):
        // max dimension median 50.8 px, p10 15.8 px, p90 166 px, max 2213 px. Filling a 512 view
        // therefore needs zoom 10x for the median shape and 32x for the tenth percentile — so an
        // 8x ceiling made the smallest shapes un-inspectable, which is exactly what the first run hit.
        const float MinZoom = 0.2f;
        const float MaxZoom = 64f;

        enum MaterialMode
        {
            BuiltinVector,
            BuiltinVectorGradient,
            CustomVertexColor,
            CustomFlatFill,

            /// <summary>
            /// The stock URP shader for <c>MeshRenderer</c> in 2D. This is the only candidate here that
            /// ships with the project today, so it is the one that answers "do we need a custom shader
            /// at all?" — and the answer is conditional: see <see cref="StockUrpMeshShader"/>.
            /// </summary>
            StockUrpMesh2D,
        }

        enum KindFilter
        {
            All,
            VectorOnly,
            PatternFilled,
            Empty,
        }

        // ── Catalog ──────────────────────────────────────────────────────────

        List<VectorShapeEntry> _all = new List<VectorShapeEntry>();
        readonly List<VectorShapeEntry> _filtered = new List<VectorShapeEntry>();
        string _filter = string.Empty;
        KindFilter _kindFilter = KindFilter.All;
        string _catalogError;
        string _catalogSummary = "not scanned";
        Vector2 _listScroll;
        int _selectedIndex = -1;

        // ── Import / tessellation settings ───────────────────────────────────

        // NOTE: these are a *chosen* baseline to sweep, not a value read out of Unity's SVGImporter.
        // VectorUtils.TessellationOptions is a plain struct whose own defaults are zero, and Unity's
        // importer does not expose its defaults through any API or doc page — so claiming these are
        // "the Unity defaults" would be a guess dressed as a fact. They are the package inspector's
        // customary starting point; the point of the sliders is that you do not have to trust them.
        VectorUtils.TessellationOptions _tess = new VectorUtils.TessellationOptions
        {
            MaxCordDeviation = 0.5f,
            MaxTanAngleDeviation = 0.1f,
            StepDistance = 0.5f,
            SamplingStepSize = 0.2f,
        };

        // NOTE: there is deliberately no "gradient resolution" setting. That knob belonged to
        // VectorUtils.BuildSprite, which is unusable here (see ImportSelected). The atlas path only
        // exposes a raster size, so "Atlas raster size" is now the single gradient-quality control.
        uint _atlasRasterSize = 1024;
        bool _buildAtlas = true;
        bool _flipY = true;
        bool _preserveViewport = true;
        MaterialMode _materialMode = MaterialMode.CustomVertexColor;

        // ── Current shape result ─────────────────────────────────────────────

        VectorShapeEntry _selected;
        Mesh _mesh;
        List<VectorUtils.Geometry> _geoms;
        VectorUtils.TextureAtlas _atlas;
        Material _material;
        string _shaderName = "(none)";
        string _importError;

        /// <summary>Non-fatal caveat about the built mesh (e.g. it exceeds the UInt16 index limit).</summary>
        string _meshWarning;

        /// <summary>Which candidate shaders exist in this install. Probed once — see <see cref="ProbeShaders"/>.</summary>
        Dictionary<string, bool> _shaderAvailable;

        int _vertexCount;
        int _triangleCount;
        float _shapeWidthPx;
        float _shapeHeightPx;
        double _lastImportMs;
        double _lastTessellateMs;

        // ── Preview scene ────────────────────────────────────────────────────

        GameObject _previewRoot;
        GameObject _rendererGo;
        MeshFilter _meshFilter;
        MeshRenderer _meshRenderer;
        Camera _previewCamera;
        RenderTexture _previewRt;
        bool _sceneReady;
        int _rtSize = 512;
        float _zoom = 4f;
        Vector2 _pan;
        bool _dragging;

        // ── Comparison ───────────────────────────────────────────────────────

        Texture2D _bakeLow;    // 1 px per SVG px  -> what the port's PNGs are
        Texture2D _bakeHigh;   // N px per SVG px  -> what the vector can be
        int _compareScale = 4;
        Texture2D _referenceTexture;

        // ── Contact sheet ────────────────────────────────────────────────────

        readonly List<int> _sheetIds = new List<int>();
        Vector2 _sheetScroll;
        int _sheetCell = 128;

        // ── Misc ─────────────────────────────────────────────────────────────

        Vector2 _mainScroll;
        string _status = string.Empty;
        bool _showDiagnostics = true;

        /// <summary>Set when the selection changed without importing — see <see cref="OnEnable"/>.</summary>
        bool _needsImport;

        // ── Lifetime ─────────────────────────────────────────────────────────

        [MenuItem("PFE/Art/Vector Sample Preview")]
        public static void ShowWindow()
        {
            var window = GetWindow<VectorSampleWindow>("Vector Sample");
            window.minSize = new Vector2(900, 600);
        }

        void OnEnable()
        {
            // Deliberately does NOT import here. ImportSelected() creates a hide-and-dont-save
            // GameObject, and OnEnable runs on every domain reload — so building the scene from
            // OnEnable means allocating objects while the editor is mid-reload. Scan only; the first
            // OnGUI pass does the import.
            // Probe shaders here too so the availability list is populated before the first repaint of
            // the Pipeline panel — a MISSING built-in shader is the single most likely surprise here.
            _shaderAvailable = ProbeShaders();
            Rescan();
        }

        void OnDisable()
        {
            CleanupPreviewScene();
            ReleaseMaterial();
            ReleaseMesh();
            ReleaseBakes();
        }

        void OnDestroy()
        {
            CleanupPreviewScene();
            ReleaseMaterial();
            ReleaseMesh();
            ReleaseBakes();
        }

        // ── Catalog ──────────────────────────────────────────────────────────

        void Rescan()
        {
            string root = SourceImportPaths.ShapesRoot;
            string error;
            _all = VectorShapeCatalog.Scan(root, out error);
            _catalogError = error;

            int vectorOnly = 0, patternFilled = 0, empty = 0, unreadable = 0;
            foreach (var e in _all)
            {
                if (!string.IsNullOrEmpty(e.Error)) unreadable++;
                switch (e.Kind)
                {
                    case VectorShapeKind.VectorOnly: vectorOnly++; break;
                    case VectorShapeKind.PatternFilled: patternFilled++; break;
                    default: empty++; break;
                }
            }

            _catalogSummary = string.Format(
                CultureInfo.InvariantCulture,
                "{0} files — {1} vector-only, {2} pattern+bitmap, {3} empty{4}",
                _all.Count, vectorOnly, patternFilled, empty,
                unreadable > 0 ? ", " + unreadable + " unreadable" : string.Empty);

            ApplyFilter();

            if (_all.Count == 0)
            {
                _status = "No shapes found. Resolve the source root via PFE/Data/Import Source Root…";
                return;
            }

            // Land on something drawable rather than on whatever sorts first.
            int first = _filtered.FindIndex(e => e.Kind == VectorShapeKind.VectorOnly);
            SelectIndex(first >= 0 ? first : 0, deferImport: true);
        }

        void ApplyFilter()
        {
            _filtered.Clear();
            string needle = (_filter ?? string.Empty).Trim();
            bool numeric = needle.Length > 0 && int.TryParse(needle, NumberStyles.None,
                                                            CultureInfo.InvariantCulture, out _);

            foreach (var e in _all)
            {
                if (_kindFilter == KindFilter.VectorOnly && e.Kind != VectorShapeKind.VectorOnly) continue;
                if (_kindFilter == KindFilter.PatternFilled && e.Kind != VectorShapeKind.PatternFilled) continue;
                if (_kindFilter == KindFilter.Empty && e.Kind != VectorShapeKind.Empty) continue;

                if (needle.Length > 0)
                {
                    if (numeric)
                    {
                        if (e.ShapeId.ToString(CultureInfo.InvariantCulture)
                             .IndexOf(needle, StringComparison.Ordinal) < 0) continue;
                    }
                    else if (e.DisplayName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0 &&
                             e.KindLabel.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                }

                _filtered.Add(e);
            }
        }

        void SelectIndex(int index, bool deferImport = false)
        {
            if (_filtered.Count == 0) return;
            _selectedIndex = Mathf.Clamp(index, 0, _filtered.Count - 1);
            _selected = _filtered[_selectedIndex];

            if (deferImport) _needsImport = true;
            else ImportSelected();
        }

        // ── Import + tessellate ──────────────────────────────────────────────

        void ImportSelected()
        {
            _importError = null;
            _meshWarning = null;
            // Clear rather than null: the Mesh is reused across imports, and an empty mesh draws
            // nothing, which is what a failed import should look like.
            if (_mesh != null) _mesh.Clear();
            _geoms = null;
            _atlas = null;
            _vertexCount = 0;
            _triangleCount = 0;
            _shapeWidthPx = 0f;
            _shapeHeightPx = 0f;
            ReleaseBakes();

            if (_selected == null) return;

            // A bitmap-filled shape is *only* a fill if the atlas is built — with no atlas it renders
            // as bare geometry. Flip the switch rather than let the user conclude the fill was lost.
            if (_selected.Kind == VectorShapeKind.PatternFilled)
                _buildAtlas = true;

            string svgText;
            try
            {
                svgText = File.ReadAllText(_selected.FullPath);
            }
            catch (Exception ex)
            {
                _importError = "Read failed: " + ex.GetType().Name + ": " + ex.Message;
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                float w = _selected.RootWidth > 0f ? _selected.RootWidth : 256f;
                float h = _selected.RootHeight > 0f ? _selected.RootHeight : 256f;

                SVGParser.SceneInfo info;
                using (var reader = new StringReader(svgText))
                {
                    info = SVGParser.ImportSVG(
                        reader,
                        _preserveViewport ? ViewportOptions.PreserveViewport : ViewportOptions.DontPreserve,
                        1f,
                        SvgPixelsPerUnit,
                        Mathf.Max(1, Mathf.CeilToInt(w)),
                        Mathf.Max(1, Mathf.CeilToInt(h)));
                }

                if (info.Scene == null || info.Scene.Root == null)
                {
                    _importError = "SVGParser produced an empty scene. (Check the Console for SVG Error lines.)";
                    return;
                }

                _geoms = VectorUtils.TessellateScene(info.Scene, _tess, info.NodeOpacity);
                if (_geoms == null || _geoms.Count == 0)
                {
                    _importError = "Tessellation produced no geometry. Loosen Max Cord Deviation / Step Distance.";
                    return;
                }

                foreach (var g in _geoms)
                {
                    if (g.Vertices != null) _vertexCount += g.Vertices.Length;
                    if (g.Indices != null) _triangleCount += g.Indices.Length / 3;
                }

                _lastTessellateMs = sw.Elapsed.TotalMilliseconds;

                // Atlas BEFORE the mesh: GenerateAtlasAndFillUVs writes the UVs into the geometry and
                // FillMesh copies them into the mesh. Reversed, the mesh gets dead UVs.
                if (_buildAtlas)
                    _atlas = VectorUtils.GenerateAtlasAndFillUVs(_geoms, _atlasRasterSize);

                // FillMesh, NOT BuildSprite. Measured on 2026-10-05 in Unity 6000.3.10f1:
                //     "Not allowed to override geometry on sprite ''"
                //     UnityEngine.Sprite:OverrideGeometry -> VectorUtils:BuildSprite
                // BuildSprite ends in Sprite.OverrideGeometry, which Unity refuses here. The refusal
                // leaves the sprite with an EMPTY mesh, and the next camera render then reports
                // "Incomplete mesh data in Sprite" — which is a symptom, not the fault. A Mesh +
                // MeshRenderer has no such gate, and it also removes the sprite pivot/rect layer that
                // was corrupting the framing arithmetic (a 4.7x1.5 px shape was reported as 0.7x0.1).
                if (_mesh == null)
                    _mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                _mesh.Clear();
                VectorUtils.FillMesh(_mesh, _geoms, SvgPixelsPerUnit, _flipY);
                _mesh.RecalculateBounds();

                if (_mesh.vertexCount == 0)
                {
                    _importError = "FillMesh produced an empty mesh.";
                    return;
                }

                // FillMesh writes UInt16 indices, so past 65535 vertices the mesh cannot be
                // represented. Say so, rather than let a truncated shape read as a wrong shape.
                if (_vertexCount > 65000)
                    _meshWarning = _vertexCount + " vertices exceeds the 65535 UInt16 index limit — the mesh "
                                 + "is probably truncated. Raise Max Cord Deviation / Step Distance.";

                var bounds = _mesh.bounds.size;
                _shapeWidthPx = bounds.x * SvgPixelsPerUnit;
                _shapeHeightPx = bounds.y * SvgPixelsPerUnit;
            }
            catch (Exception ex)
            {
                _importError = ex.GetType().Name + ": " + ex.Message;
                return;
            }
            finally
            {
                _lastImportMs = sw.Elapsed.TotalMilliseconds;
            }

            BuildMaterial();
            SetupPreviewScene();
            FitToView();
            RenderPreview();

            _status = string.Format(
                CultureInfo.InvariantCulture,
                "sym{0}: {1} geoms, {2} verts, {3} tris, {4:0.#}x{5:0.#} px  ({6:0} ms import+tessellate)",
                _selected.ShapeId, _geoms.Count, _vertexCount, _triangleCount,
                _shapeWidthPx, _shapeHeightPx, _lastImportMs);
        }

        /// <summary>Every shader this window will try, in the order the Material dropdown lists them.</summary>
        static readonly string[] CandidateShaders =
        {
            "Unlit/Vector",
            "Unlit/VectorGradient",
            "PFE/Vector Sample/VertexColor",
            "PFE/Vector Sample/FlatFill",
            StockUrpMeshShader,
        };

        const string OurVertexColorShader = "PFE/Vector Sample/VertexColor";
        const string OurFlatFillShader = "PFE/Vector Sample/FlatFill";

        /// <summary>
        /// The stock URP 2D shader for mesh renderers, shipped in
        /// <c>com.unity.render-pipelines.universal</c> as
        /// <c>Shaders/2D/Mesh2D-Unlit-Default.shader</c>.
        ///
        /// <para><b>Why it is listed, and what it proves.</b> It is the only candidate that already
        /// exists in this project, so it is the honest test of "does vector geometry need a custom
        /// shader?". Read from the shipped file, it samples <c>_MainTex</c> at <c>input.uv</c> and
        /// multiplies by a <c>_White</c> material tint. Its vertex input struct
        /// (<c>COMMON_2D_INPUTS</c> in <c>Shaders/2D/Include/Core2D.hlsl</c>) declares only
        /// <c>POSITION</c>, <c>TEXCOORD0</c> and <c>NORMAL</c> — there is <b>no <c>COLOR</c>
        /// semantic</b>, and <c>CommonUnlitVertex</c> never writes one to the varyings.</para>
        ///
        /// <para>So it works on the <b>atlas</b> path (<see cref="_buildAtlas"/> true, where
        /// <c>GenerateAtlasAndFillUVs</c> puts the fills in <c>_MainTex</c> and the UVs index them)
        /// and it does <b>not</b> work on the plain vertex-colour path, where the fill lives in
        /// <c>mesh.colors32</c> and would be silently discarded — the shape would render as a flat
        /// <c>_White</c> silhouette. That failure is exactly what the FlatFill shader exists to
        /// distinguish from a geometry fault.</para>
        /// </summary>
        const string StockUrpMeshShader = "Universal Render Pipeline/2D/Mesh2D-Unlit-Default";

        // Asset file stems, used to resolve our own shaders by path rather than by name.
        const string VertexColorAssetName = "PFEVectorSampleVertexColor";
        const string FlatFillAssetName = "PFEVectorSampleFlatFill";

        /// <summary>
        /// Resolves a shader, preferring an asset load for our own two.
        ///
        /// <para><b>Why not just <c>Shader.Find</c>.</b> Both of these shaders live under an
        /// <c>Editor/</c> folder, which is exactly the case where <c>Shader.Find</c> is least reliable:
        /// it resolves through the shader database that feeds builds, and Editor-folder assets are
        /// excluded from builds. Loading the asset by path has no such ambiguity, and it fails loudly
        /// (returns null) rather than silently handing back the wrong shader. <c>Shader.Find</c> is kept
        /// as the fallback so the built-in names still get their chance.</para>
        /// </summary>
        static Shader FindShader(string shaderName)
        {
            string assetName = null;
            if (shaderName == OurVertexColorShader) assetName = VertexColorAssetName;
            else if (shaderName == OurFlatFillShader) assetName = FlatFillAssetName;

            if (assetName != null)
            {
                string[] guids = AssetDatabase.FindAssets(assetName + " t:Shader");
                foreach (string guid in guids)
                {
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
                    if (shader != null) return shader;
                }
            }

            return Shader.Find(shaderName);
        }

        static string ShaderNameFor(MaterialMode mode)
        {
            switch (mode)
            {
                case MaterialMode.BuiltinVector: return "Unlit/Vector";
                case MaterialMode.BuiltinVectorGradient: return "Unlit/VectorGradient";
                case MaterialMode.CustomVertexColor: return OurVertexColorShader;
                case MaterialMode.StockUrpMesh2D: return StockUrpMeshShader;
                default: return OurFlatFillShader;
            }
        }

        /// <summary>
        /// Which candidate shaders actually resolve. Cached — this is read on every repaint and
        /// <c>Shader.Find</c> is not free.
        ///
        /// <para><b>Why this is not cosmetic.</b> <c>Unlit/Vector</c> and <c>Unlit/VectorGradient</c> are
        /// the shaders Unity's vector-graphics documentation pairs with <c>VectorUtils</c> geometry, and
        /// they shipped with the <c>com.unity.vectorgraphics</c> <b>package</b>. The built-in module that
        /// replaced that package does not carry them. Verified offline against this install's shipped
        /// shader bundle: <c>Sprites/Default</c> and <c>Unlit/Texture</c> both resolve (positive
        /// controls) while both vector names return <b>zero</b> hits. So the built-in names are expected
        /// to be missing here — and the two <c>PFE/Vector Sample/*</c> shaders are therefore not a
        /// nice-to-have test harness, they are the piece that makes the geometry visible at all.</para>
        ///
        /// <para><b>Correction (later).</b> "The piece that makes the geometry visible at all" was too
        /// strong, and reading the URP package settled it. <c>Mesh2D-Unlit-Default</c> ships with the
        /// project, and it renders <c>VectorUtils</c> geometry fine <i>provided the atlas path is used</i>
        /// — because it samples <c>_MainTex</c> at <c>input.uv</c> and the atlas is what carries the
        /// fills. It ignores vertex colour entirely, so it cannot serve the non-atlas path. The accurate
        /// statement is: <b>a custom shader is required only if you skip the atlas.</b> This list is kept
        /// whole so both branches are one click apart.</para>
        /// </summary>
        static Dictionary<string, bool> ProbeShaders()
        {
            var map = new Dictionary<string, bool>();
            foreach (string n in CandidateShaders) map[n] = FindShader(n) != null;
            return map;
        }

        void BuildMaterial()
        {
            string shaderName = ShaderNameFor(_materialMode);
            var shader = FindShader(shaderName);

            if (shader == null)
            {
                // Fall back to ours rather than render magenta — but keep saying both what was asked
                // for and that it is missing. A silent substitution would hide the finding.
                var fallback = FindShader(OurVertexColorShader);
                if (fallback == null)
                {
                    _shaderName = shaderName + "  ⚠ NOT FOUND (and no fallback)";
                    _material = null;
                    return;
                }

                _shaderName = shaderName + "  ⚠ NOT FOUND → using " + OurVertexColorShader;
                shader = fallback;
            }
            else
            {
                _shaderName = shaderName;
            }

            if (_material != null) DestroyImmediate(_material);
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            if (_atlas != null && _atlas.Texture != null && _material.HasProperty("_MainTex"))
                _material.SetTexture("_MainTex", _atlas.Texture);
        }

        // ── Preview scene ────────────────────────────────────────────────────

        void SetupPreviewScene()
        {
            if (_sceneReady && _meshFilter != null)
            {
                _meshFilter.sharedMesh = _mesh;
                if (_material != null) _meshRenderer.sharedMaterial = _material;
                return;
            }

            // CleanupPreviewScene deliberately does NOT touch _mesh or _material: both are owned by the
            // import path and must survive a scene rebuild. Destroying the material here was a real bug
            // — BuildMaterial() runs immediately before this, so the *first* import threw away the
            // material it had just created and drew with the renderer's default.
            CleanupPreviewScene();
            if (_mesh == null) return;

            _previewRoot = new GameObject("[VectorSample_Root]")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer,
            };
            _previewRoot.transform.position = new Vector3(PreviewOffsetX, 0f, 0f);

            _rendererGo = new GameObject("vector_mesh")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer,
            };
            _rendererGo.transform.SetParent(_previewRoot.transform);
            _rendererGo.transform.localPosition = Vector3.zero;

            _meshFilter = _rendererGo.AddComponent<MeshFilter>();
            _meshFilter.sharedMesh = _mesh;

            _meshRenderer = _rendererGo.AddComponent<MeshRenderer>();
            _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;
            if (_material != null) _meshRenderer.sharedMaterial = _material;

            var camGo = new GameObject("[VectorSample_Camera]")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer,
            };
            _previewCamera = camGo.AddComponent<Camera>();
            _previewCamera.clearFlags = CameraClearFlags.SolidColor;
            // Mid grey, not black: a dark plate hides black-filled shapes, and several of these
            // gradients start at #000000.
            _previewCamera.backgroundColor = new Color(0.35f, 0.35f, 0.35f, 1f);
            _previewCamera.orthographic = true;
            _previewCamera.cullingMask = 1 << PreviewLayer;
            _previewCamera.enabled = false;

            RecreateRenderTexture();
            _sceneReady = true;
        }

        void RecreateRenderTexture()
        {
            if (_previewRt != null) DestroyImmediate(_previewRt);
            _previewRt = new RenderTexture(_rtSize, _rtSize, 16)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
        }

        /// <summary>Destroys the off-screen scene only. <c>_mesh</c> and <c>_material</c> survive — see
        /// the note in <see cref="SetupPreviewScene"/>.</summary>
        void CleanupPreviewScene()
        {
            if (_previewRoot != null) DestroyImmediate(_previewRoot);
            if (_previewCamera != null) DestroyImmediate(_previewCamera.gameObject);
            if (_previewRt != null) DestroyImmediate(_previewRt);

            _previewRoot = null;
            _rendererGo = null;
            _meshFilter = null;
            _meshRenderer = null;
            _previewCamera = null;
            _previewRt = null;
            _sceneReady = false;
        }

        void ReleaseMesh()
        {
            if (_mesh != null) DestroyImmediate(_mesh);
            _mesh = null;
        }

        void ReleaseMaterial()
        {
            if (_material != null) DestroyImmediate(_material);
            _material = null;
        }

        /// <summary>True when there is geometry worth rendering or framing.</summary>
        bool HasGeometry => _mesh != null && _mesh.vertexCount > 0;

        void ReleaseBakes()
        {
            if (_bakeLow != null) DestroyImmediate(_bakeLow);
            if (_bakeHigh != null) DestroyImmediate(_bakeHigh);
            _bakeLow = null;
            _bakeHigh = null;
        }

        /// <summary>
        /// Zoom is defined as <b>render-texture pixels per SVG pixel</b>, so 1x here means the same
        /// sampling the port's imported PNGs have. Orthographic size is derived from it rather than
        /// picked by eye — otherwise "zoom 4" would mean nothing in particular.
        /// </summary>
        float ZoomToOrthoSize()
        {
            return _rtSize / (2f * SvgPixelsPerUnit * Mathf.Max(0.01f, _zoom));
        }

        float FitZoom()
        {
            if (_shapeWidthPx <= 0f || _shapeHeightPx <= 0f) return 4f;
            return Mathf.Min(_rtSize / _shapeWidthPx, _rtSize / _shapeHeightPx);
        }

        void FitToView()
        {
            _zoom = Mathf.Clamp(FitZoom(), MinZoom, MaxZoom);
            _pan = Vector2.zero;
        }

        void RenderPreview()
        {
            if (!_sceneReady || _previewCamera == null || _previewRt == null || !HasGeometry) return;

            if (_previewRt.width != _rtSize) RecreateRenderTexture();

            var center = _mesh.bounds.center;
            _previewCamera.orthographicSize = ZoomToOrthoSize();
            _previewCamera.transform.position = new Vector3(
                PreviewOffsetX + center.x + _pan.x,
                center.y + _pan.y,
                -10f);

            _previewCamera.targetTexture = _previewRt;
            _previewCamera.Render();
            _previewCamera.targetTexture = null;
        }

        // ── Comparison bakes ─────────────────────────────────────────────────

        /// <summary>
        /// Bakes the shape twice at its own aspect ratio: once at 1 pixel per SVG pixel (what a PNG
        /// import gives you) and once at <see cref="_compareScale"/>x (what the vector gives you).
        /// Both are then drawn into equal on-screen rectangles, so the only difference on screen is the
        /// sampling density.
        /// </summary>
        void BuildBakes()
        {
            ReleaseBakes();
            if (!HasGeometry || _shapeWidthPx <= 0f || _shapeHeightPx <= 0f) return;

            _bakeLow = Bake(1);
            _bakeHigh = Bake(_compareScale);

            if (_bakeLow != null) _bakeLow.filterMode = FilterMode.Point;
            if (_bakeHigh != null) _bakeHigh.filterMode = FilterMode.Bilinear;
        }

        /// <summary>
        /// Renders the shape at exactly <paramref name="scale"/> render-texture pixels per SVG pixel,
        /// into a texture sized to the shape's own bounds — so the 1x and Nx bakes share a framing and
        /// differ only in sampling density.
        ///
        /// <para>Goes through the preview camera rather than <c>VectorUtils.RenderSpriteToTexture2D</c>:
        /// that API takes a <c>Sprite</c>, and the sprite path is precisely what is broken here (see
        /// <see cref="ImportSelected"/>). Using the camera also means the comparison is produced by the
        /// same render path as the preview, which is the whole point of a comparison.</para>
        /// </summary>
        Texture2D Bake(int scale)
        {
            if (_previewCamera == null || !HasGeometry) return null;

            int w = Mathf.Max(1, Mathf.RoundToInt(_shapeWidthPx * scale));
            int h = Mathf.Max(1, Mathf.RoundToInt(_shapeHeightPx * scale));

            // Cap the longest side; a 1.99 MB SVG can be enormous.
            const int MaxDim = 2048;
            int longest = Mathf.Max(w, h);
            if (longest > MaxDim)
            {
                float k = MaxDim / (float)longest;
                w = Mathf.Max(1, Mathf.RoundToInt(w * k));
                h = Mathf.Max(1, Mathf.RoundToInt(h * k));
            }

            var rt = RenderTexture.GetTemporary(w, h, 16);
            var savedTarget = _previewCamera.targetTexture;
            float savedSize = _previewCamera.orthographicSize;
            Vector3 savedPos = _previewCamera.transform.position;
            var savedActive = RenderTexture.active;

            Texture2D tex = null;
            try
            {
                var center = _mesh.bounds.center;

                // The RT is h px tall and must cover h/scale SVG px = h/(scale*ppu) world units, so the
                // half-height (orthographic size) is that over two.
                _previewCamera.orthographicSize = h / (2f * SvgPixelsPerUnit * scale);
                _previewCamera.transform.position = new Vector3(PreviewOffsetX + center.x, center.y, -10f);

                _previewCamera.targetTexture = rt;
                _previewCamera.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
                tex.Apply();
                tex.hideFlags = HideFlags.HideAndDontSave;
            }
            catch (Exception ex)
            {
                _status = "Bake failed at " + scale + "x: " + ex.GetType().Name + ": " + ex.Message;
                if (tex != null) { DestroyImmediate(tex); tex = null; }
            }
            finally
            {
                _previewCamera.targetTexture = savedTarget;
                _previewCamera.orthographicSize = savedSize;
                _previewCamera.transform.position = savedPos;
                RenderTexture.active = savedActive;
                RenderTexture.ReleaseTemporary(rt);
            }

            return tex;
        }

        // ── Contact sheet ────────────────────────────────────────────────────

        /// <summary>
        /// Bakes every shape in the sheet list into one grid PNG. This is the "multiple examples" view:
        /// a single image that shows a flat fill, a radial gradient, a linear gradient, a stroked
        /// outline and a bitmap-filled shape side by side, at matched scale.
        ///
        /// <para>Uses whatever material mode the panel is set to, deliberately — the sheet is a
        /// comparison of <i>shapes</i>, so holding the render path constant is the point. If you want
        /// to see the atlas path fail, switch the mode and re-render; do not make the sheet silently
        /// pick a mode of its own.</para>
        /// </summary>
        void RenderContactSheet()
        {
            if (_sheetIds.Count == 0)
            {
                _status = "Contact sheet is empty — add shapes with “Add current”.";
                return;
            }

            int count = _sheetIds.Count;
            int cols = Mathf.CeilToInt(Mathf.Sqrt(count));
            int rows = Mathf.CeilToInt(count / (float)cols);
            int cell = Mathf.Max(16, _sheetCell);
            int sheetW = cols * cell;
            int sheetH = rows * cell;

            // One CPU-side buffer for the whole sheet. Reading it back per cell would copy a megabyte
            // per shape for no reason.
            var pixels = new Color32[sheetW * sheetH];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(40, 40, 44, 255);

            var report = new StringBuilder();
            var saved = _selected;
            int placed = 0;

            foreach (int id in _sheetIds)
            {
                var entry = _all.Find(e => e.ShapeId == id);
                if (entry == null)
                {
                    report.AppendLine("  sym" + id + ": NOT IN CATALOG (skipped)");
                    continue;
                }

                _selected = entry;
                ImportSelected();

                if (!HasGeometry)
                {
                    report.AppendLine("  sym" + id + " (" + entry.KindLabel + "): FAILED — " + (_importError ?? "unknown"));
                    continue;
                }

                var bake = Bake(1);
                if (bake == null)
                {
                    report.AppendLine("  sym" + id + ": bake returned null");
                    continue;
                }

                // Fit the bake into its cell, preserving aspect.
                float k = Mathf.Min(cell / (float)bake.width, cell / (float)bake.height);
                int dw = Mathf.Max(1, Mathf.RoundToInt(bake.width * k));
                int dh = Mathf.Max(1, Mathf.RoundToInt(bake.height * k));

                int col = placed % cols;
                int row = placed / cols;
                // Texture2D y=0 is the BOTTOM, so row 0 has to be placed at the highest y to read
                // top-down in the saved PNG.
                int originX = col * cell + (cell - dw) / 2;
                int originY = (rows - 1 - row) * cell + (cell - dh) / 2;

                var src = bake.GetPixels32();
                for (int y = 0; y < dh; y++)
                {
                    int sy = Mathf.Clamp(Mathf.RoundToInt(y / k), 0, bake.height - 1);
                    int dy = originY + y;
                    if (dy < 0 || dy >= sheetH) continue;
                    for (int x = 0; x < dw; x++)
                    {
                        int sx = Mathf.Clamp(Mathf.RoundToInt(x / k), 0, bake.width - 1);
                        int dx = originX + x;
                        if (dx < 0 || dx >= sheetW) continue;
                        pixels[dy * sheetW + dx] = src[sy * bake.width + sx];
                    }
                }

                DestroyImmediate(bake);
                report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  cell {0} (r{1}c{2}): sym{3}  {4}  {5}x{6}px  {7} geoms",
                    placed, row, col, id, entry.KindLabel, bake.width, bake.height, _geoms?.Count ?? 0));
                placed++;
            }

            string path = Path.Combine(ExportRoot, "vector_contact_sheet.png");
            string message;
            var sheet = new Texture2D(sheetW, sheetH, TextureFormat.RGBA32, false);
            try
            {
                sheet.SetPixels32(pixels);
                sheet.Apply();

                EnsureDirectory(ExportRoot);
                File.WriteAllBytes(Path.GetFullPath(path), sheet.EncodeToPNG());
                AssetDatabase.Refresh();

                message = "Contact sheet: " + path + "  (" + placed + "/" + count + " cells, " + sheetW + "x" + sheetH + ")\n" + report;
            }
            catch (Exception ex)
            {
                message = "Contact sheet write failed: " + ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                DestroyImmediate(sheet);
                // Restore the user's selection — and note that ImportSelected() writes _status, so the
                // message has to be assigned AFTER it or the sheet result is silently replaced.
                _selected = saved;
                ImportSelected();
            }

            _status = message;
        }

        // ── GUI ──────────────────────────────────────────────────────────────

        void OnGUI()
        {
            // Deferred first import: OnEnable only scans, so the scene is built here instead.
            if (_needsImport && _selected != null)
            {
                _needsImport = false;
                ImportSelected();
                RenderPreview();
            }

            _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);

            DrawHeader();

            if (_all.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    string.IsNullOrEmpty(_catalogError)
                        ? "No shape SVGs found under:\n" + SourceImportPaths.ShapesRoot
                        : _catalogError,
                    MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.30f));
            DrawShapeList();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.34f));
            DrawPreviewPanel();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical();
            DrawPipelinePanel();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            DrawComparisonPanel();

            EditorGUILayout.Space(4);
            DrawSheetPanel();

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_status, MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawHeader()
        {
            EditorGUILayout.LabelField("AS3 Vector Sample Preview", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("shapes root", SourceImportPaths.ShapesRoot, EditorStyles.miniLabel);
            EditorGUILayout.LabelField("catalog", _catalogSummary, EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginChangeCheck();
            _filter = EditorGUILayout.TextField("Filter", _filter, GUILayout.Width(220));
            _kindFilter = (KindFilter)EditorGUILayout.EnumPopup(_kindFilter, GUILayout.Width(120));
            if (EditorGUI.EndChangeCheck())
            {
                ApplyFilter();
                if (_filtered.Count > 0) SelectIndex(0);
            }

            if (GUILayout.Button("Rescan", GUILayout.Width(70))) Rescan();
            if (GUILayout.Button("Copy report", GUILayout.Width(90)))
            {
                EditorGUIUtility.systemCopyBuffer = BuildReport();
                _status = "Report copied to clipboard.";
            }

            EditorGUILayout.LabelField(_filtered.Count + " / " + _all.Count, EditorStyles.miniLabel, GUILayout.Width(90));

            EditorGUILayout.EndHorizontal();
        }

        void DrawShapeList()
        {
            EditorGUILayout.LabelField("Shapes", EditorStyles.boldLabel);

            _listScroll = EditorGUILayout.BeginScrollView(_listScroll, GUILayout.ExpandHeight(true));

            // Virtualised: an unfiltered list is 2563 rows, and drawing every one of them on every
            // repaint is the difference between a usable list and a sticky one.
            int visible = Mathf.CeilToInt(position.height / RowHeight) + 2;
            int first = Mathf.Max(0, Mathf.FloorToInt(_listScroll.y / RowHeight));
            int last = Mathf.Min(_filtered.Count - 1, first + visible);

            if (first > 0) GUILayout.Space(first * RowHeight);

            for (int i = first; i <= last; i++)
                DrawShapeRow(i);

            if (last < _filtered.Count - 1)
                GUILayout.Space((_filtered.Count - 1 - last) * RowHeight);

            EditorGUILayout.EndScrollView();
        }

        void DrawShapeRow(int i)
        {
            var e = _filtered[i];
            var rect = GUILayoutUtility.GetRect(0f, RowHeight, GUILayout.ExpandWidth(true));

            if (Event.current.type == EventType.Repaint)
            {
                if (i == _selectedIndex)
                    EditorGUI.DrawRect(rect, new Color(0.24f, 0.48f, 0.90f, 0.35f));
                else if ((i & 1) == 0)
                    EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.03f));
            }

            if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
                SelectIndex(i);

            string label = string.Format(CultureInfo.InvariantCulture,
                "{0}   {1}   {2}   {3}K",
                e.DisplayName, e.KindLabel, e.SizeLabel, e.Bytes / 1024);
            if (!string.IsNullOrEmpty(e.Error)) label += "   ⚠ " + e.Error;

            GUI.Label(rect, label, EditorStyles.miniLabel);
        }

        void DrawPreviewPanel()
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            if (_sceneReady && _previewRt != null)
            {
                float avail = position.width * 0.32f;
                float size = Mathf.Min(avail, _rtSize);
                var rect = GUILayoutUtility.GetRect(size, size);

                EditorGUI.DrawPreviewTexture(rect, _previewRt, null, ScaleMode.ScaleToFit);

                // Drag to pan — at 8x the shape overflows the render texture and panning is the only
                // way to reach the rest of it.
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Pan);
                var evt = Event.current;
                // Left button only — a right-click drag is the editor's own gesture, not ours.
                if (evt.type == EventType.MouseDown && evt.button == 0 && rect.Contains(evt.mousePosition))
                {
                    _dragging = true;
                    evt.Use();
                }
                else if (evt.type == EventType.MouseUp && evt.button == 0 && _dragging)
                {
                    _dragging = false;
                    evt.Use();
                }
                else if (evt.type == EventType.MouseLeaveWindow && _dragging)
                {
                    // Otherwise releasing the button off the rect leaves us stuck in drag.
                    _dragging = false;
                }
                else if (evt.type == EventType.MouseDrag && _dragging)
                {
                    float worldPerPixel = (2f * ZoomToOrthoSize()) / _rtSize;
                    _pan -= evt.delta * worldPerPixel;
                    RenderPreview();
                    Repaint();
                    evt.Use();
                }
            }
            else
            {
                var rect = GUILayoutUtility.GetRect(200f, 200f);
                EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f));
                GUI.Label(rect, _importError ?? "Nothing imported", EditorStyles.centeredGreyMiniLabel);
            }

            if (!string.IsNullOrEmpty(_importError))
                EditorGUILayout.HelpBox(_importError, MessageType.Error);

            EditorGUI.BeginChangeCheck();

            // Logarithmic: the useful range spans 0.2x to 64x, and on a linear slider 1x would sit in
            // the first ~1% of the travel and be impossible to hit deliberately.
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Zoom (px per SVG px)", GUILayout.Width(140));
            float logZoom = EditorGUILayout.Slider(Mathf.Log(_zoom), Mathf.Log(MinZoom), Mathf.Log(MaxZoom));
            _zoom = Mathf.Exp(logZoom);
            EditorGUILayout.LabelField(_zoom.ToString("0.##", CultureInfo.InvariantCulture) + "x",
                                       GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Fit", GUILayout.Width(40))) { FitToView(); }
            if (GUILayout.Button("1x", GUILayout.Width(40))) { _zoom = 1f; _pan = Vector2.zero; }
            if (GUILayout.Button("4x", GUILayout.Width(40))) { _zoom = 4f; _pan = Vector2.zero; }
            if (GUILayout.Button("16x", GUILayout.Width(46))) { _zoom = 16f; _pan = Vector2.zero; }
            EditorGUILayout.LabelField("Render size", GUILayout.Width(70));
            _rtSize = EditorGUILayout.IntSlider(_rtSize, 128, 1024);
            EditorGUILayout.EndHorizontal();

            if (EditorGUI.EndChangeCheck()) RenderPreview();

            EditorGUILayout.LabelField(string.Format(CultureInfo.InvariantCulture,
                "showing {0:0.#} px of {1:0.#}x{2:0.#}  (camera size {3:0.###})",
                2f * ZoomToOrthoSize() * SvgPixelsPerUnit, _shapeWidthPx, _shapeHeightPx, ZoomToOrthoSize()),
                EditorStyles.miniLabel);
        }

        void DrawPipelinePanel()
        {
            EditorGUILayout.LabelField("Pipeline", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            _materialMode = (MaterialMode)EditorGUILayout.EnumPopup("Material", _materialMode);
            EditorGUILayout.LabelField("shader", _shaderName, EditorStyles.miniLabel);

            _buildAtlas = EditorGUILayout.ToggleLeft(
                "Build atlas (gradients + bitmap fills)", _buildAtlas);
            EditorGUI.BeginDisabledGroup(!_buildAtlas);
            _atlasRasterSize = (uint)EditorGUILayout.IntPopup("Atlas raster size", (int)_atlasRasterSize,
                new[] { "256", "512", "1024", "2048" }, new[] { 256, 512, 1024, 2048 });
            EditorGUI.EndDisabledGroup();

            // The stock URP shader reads its colour from _MainTex at the vertex UVs and has no COLOR
            // input at all (verified in Core2D.hlsl — COMMON_2D_INPUTS declares POSITION, TEXCOORD0,
            // NORMAL only). Without the atlas there are no meaningful UVs and no _MainTex, so the shape
            // would render as a flat tint. Say so here rather than letting it look like a geometry bug.
            if (_materialMode == MaterialMode.StockUrpMesh2D && !_buildAtlas)
                EditorGUILayout.HelpBox(
                    "Mesh2D-Unlit-Default ignores vertex colour, so it needs the atlas: the fills live in "
                    + "_MainTex and the UVs index them. With \"Build atlas\" off you will see a flat _White "
                    + "silhouette — that is the shader's input struct, not a fault in the mesh.",
                    MessageType.Warning);

            // Symmetrically: the flat-fill shader never samples _MainTex, so with the atlas on it
            // deliberately discards the fills. That is its whole purpose (it separates an atlas/UV
            // fault from a geometry fault), but it is worth stating so it is not misread as a bug.
            if (_materialMode == MaterialMode.CustomFlatFill && _buildAtlas)
                EditorGUILayout.HelpBox(
                    "FlatFill never samples _MainTex, so with the atlas on it draws a flat silhouette by "
                    + "design. It is the control that tells an atlas/UV fault apart from a geometry fault.",
                    MessageType.Info);

            // "Gradient resolution" used to sit here. It was a BuildSprite parameter, and BuildSprite is
            // unusable in this Unity version (see ImportSelected) — so "Atlas raster size" above is now
            // the only gradient-quality control. A slider that changes nothing is worse than no slider.

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Shader availability", EditorStyles.miniLabel);
            EditorGUI.indentLevel++;
            if (_shaderAvailable == null) _shaderAvailable = ProbeShaders();
            foreach (string candidate in CandidateShaders)
            {
                bool found;
                bool ok = _shaderAvailable.TryGetValue(candidate, out found) && found;
                EditorGUILayout.LabelField((ok ? "found    " : "MISSING  ") + candidate, EditorStyles.miniLabel);
            }
            EditorGUI.indentLevel--;

            _flipY = EditorGUILayout.ToggleLeft("Flip Y axis (SVG is Y-down)", _flipY);
            _preserveViewport = EditorGUILayout.ToggleLeft("Preserve viewport", _preserveViewport);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Tessellation", EditorStyles.miniLabel);
            EditorGUI.indentLevel++;
            _tess.MaxCordDeviation = EditorGUILayout.Slider("Max cord deviation", _tess.MaxCordDeviation, 0f, 4f);
            _tess.MaxTanAngleDeviation = EditorGUILayout.Slider("Max tangent angle", _tess.MaxTanAngleDeviation, 0f, 45f);
            _tess.StepDistance = EditorGUILayout.Slider("Step distance", _tess.StepDistance, 0f, 8f);
            _tess.SamplingStepSize = EditorGUILayout.Slider("Sampling step size", _tess.SamplingStepSize, 0f, 1f);
            EditorGUI.indentLevel--;

            if (EditorGUI.EndChangeCheck())
            {
                ImportSelected();
                RenderPreview();
            }

            if (GUILayout.Button("Re-import")) { ImportSelected(); RenderPreview(); }

            EditorGUILayout.Space(4);
            _showDiagnostics = EditorGUILayout.Foldout(_showDiagnostics, "Diagnostics", true);
            if (_showDiagnostics && _selected != null)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("file", _selected.FileName, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("kind", _selected.KindLabel, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("paths / radial / linear",
                    _selected.PathCount + " / " + _selected.RadialGradientCount + " / " + _selected.LinearGradientCount,
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField("patterns / embedded images",
                    _selected.PatternCount + " / " + _selected.EmbeddedImageCount, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("geoms / verts / tris",
                    (_geoms?.Count ?? 0) + " / " + _vertexCount + " / " + _triangleCount, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("atlas",
                    _atlas != null && _atlas.Texture != null
                        ? _atlas.Texture.width + "x" + _atlas.Texture.height + "  (" + _atlas.Entries.Count + " entries)"
                        : "(none)",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField("import+tessellate ms",
                    _lastImportMs.ToString("0.#", CultureInfo.InvariantCulture), EditorStyles.miniLabel);
                EditorGUILayout.LabelField("tessellate ms",
                    _lastTessellateMs.ToString("0.#", CultureInfo.InvariantCulture), EditorStyles.miniLabel);

                // Cross-check the two independent statements of the shape's size. The SVG root gives
                // one; the tessellated mesh bounds give the other. They should agree, and a mismatch is
                // the cheapest possible signal that the scale, the viewport or flipY is wrong — which
                // is exactly the class of bug that silently produced "0.7x0.1" for a 4.7x1.5 shape.
                EditorGUILayout.LabelField("svg root vs mesh px",
                    _selected.SizeLabel + "  vs  "
                    + _shapeWidthPx.ToString("0.#", CultureInfo.InvariantCulture) + "x"
                    + _shapeHeightPx.ToString("0.#", CultureInfo.InvariantCulture),
                    EditorStyles.miniLabel);

                if (!string.IsNullOrEmpty(_meshWarning))
                    EditorGUILayout.HelpBox(_meshWarning, MessageType.Warning);

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("Export preview PNG", GUILayout.Height(24)))
                ExportPreviewPng();
        }

        void DrawComparisonPanel()
        {
            EditorGUILayout.LabelField("Raster vs vector — same on-screen size", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Build comparison", GUILayout.Width(130))) BuildBakes();

            EditorGUI.BeginChangeCheck();
            _compareScale = EditorGUILayout.IntSlider("Vector scale", _compareScale, 2, 8);
            // Re-bake on change: leaving the two panels showing different scales would make the
            // comparison lie, which is worse than a slightly slower slider.
            if (EditorGUI.EndChangeCheck() && HasGeometry) BuildBakes();

            _referenceTexture = (Texture2D)EditorGUILayout.ObjectField(
                "Reference PNG (optional)", _referenceTexture, typeof(Texture2D), false);

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            DrawCompareSlot("1x raster (what the port has)", _bakeLow);
            DrawCompareSlot("vector at " + _compareScale + "x", _bakeHigh);
            if (_referenceTexture != null)
                DrawCompareSlot("reference PNG", _referenceTexture);

            EditorGUILayout.EndHorizontal();
        }

        void DrawCompareSlot(string caption, Texture2D tex)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(200));
            EditorGUILayout.LabelField(caption, EditorStyles.miniLabel);

            var rect = GUILayoutUtility.GetRect(190f, 190f);
            if (tex != null)
            {
                EditorGUI.DrawPreviewTexture(rect, tex, null, ScaleMode.ScaleToFit);
                EditorGUILayout.LabelField(tex.width + "x" + tex.height, EditorStyles.miniLabel);
            }
            else
            {
                EditorGUI.DrawRect(rect, new Color(0.22f, 0.22f, 0.22f));
                GUI.Label(rect, "—", EditorStyles.centeredGreyMiniLabel);
                EditorGUILayout.LabelField(" ", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        void DrawSheetPanel()
        {
            EditorGUILayout.LabelField("Contact sheet (multiple examples)", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add current", GUILayout.Width(110)))
            {
                if (_selected != null && !_sheetIds.Contains(_selected.ShapeId))
                    _sheetIds.Add(_selected.ShapeId);
            }
            if (GUILayout.Button("Clear", GUILayout.Width(70))) _sheetIds.Clear();
            _sheetCell = EditorGUILayout.IntSlider("Cell px", _sheetCell, 32, 512, GUILayout.Width(240));
            EditorGUI.BeginDisabledGroup(_sheetIds.Count == 0);
            if (GUILayout.Button("Render contact sheet → PNG", GUILayout.Height(22)))
                RenderContactSheet();
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (_sheetIds.Count > 0)
            {
                _sheetScroll = EditorGUILayout.BeginScrollView(_sheetScroll, GUILayout.Height(52f));
                EditorGUILayout.BeginHorizontal();
                for (int i = 0; i < _sheetIds.Count; i++)
                {
                    if (GUILayout.Button("sym" + _sheetIds[i] + "  ✕", EditorStyles.miniButton, GUILayout.Width(84)))
                    {
                        _sheetIds.RemoveAt(i);
                        i--;
                    }
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndScrollView();
            }
        }

        // ── Export / report ──────────────────────────────────────────────────

        void ExportPreviewPng()
        {
            if (_previewRt == null)
            {
                _status = "Nothing to export.";
                return;
            }

            EnsureDirectory(ExportRoot);
            string name = "sym" + (_selected != null ? _selected.ShapeId.ToString(CultureInfo.InvariantCulture) : "0")
                        + "_z" + _zoom.ToString("0.##", CultureInfo.InvariantCulture) + ".png";
            string path = Path.Combine(ExportRoot, name);

            var prev = RenderTexture.active;
            RenderTexture.active = _previewRt;
            var tex = new Texture2D(_previewRt.width, _previewRt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0f, 0f, _previewRt.width, _previewRt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            try
            {
                File.WriteAllBytes(Path.GetFullPath(path), tex.EncodeToPNG());
                AssetDatabase.Refresh();
                _status = "Exported " + path;
            }
            catch (Exception ex)
            {
                _status = "Export failed: " + ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                DestroyImmediate(tex);
            }
        }

        /// <summary>
        /// A paste-backable summary. Written because the interesting answers here are numbers, and a
        /// screenshot of a preview window cannot carry them.
        /// </summary>
        string BuildReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== AS3 vector sample report ===");
            sb.AppendLine("shapes root : " + SourceImportPaths.ShapesRoot);
            sb.AppendLine("catalog     : " + _catalogSummary);
            sb.AppendLine("filter      : " + (_filter ?? string.Empty) + "  kind=" + _kindFilter);
            sb.AppendLine();
            if (_selected != null)
            {
                sb.AppendLine("shape       : sym" + _selected.ShapeId + "  (" + _selected.FileName + ")");
                sb.AppendLine("kind        : " + _selected.KindLabel);
                sb.AppendLine("paths       : " + _selected.PathCount);
                sb.AppendLine("gradients   : radial=" + _selected.RadialGradientCount + " linear=" + _selected.LinearGradientCount);
                sb.AppendLine("patterns    : " + _selected.PatternCount + "  embedded images=" + _selected.EmbeddedImageCount);
                sb.AppendLine("svg size px : " + _selected.SizeLabel);
            }
            sb.AppendLine();
            sb.AppendLine("shader      : " + _shaderName);
            sb.AppendLine("material    : " + _materialMode);
            sb.AppendLine("atlas       : " + (_buildAtlas
                ? _atlasRasterSize + " raster, " + (_atlas != null && _atlas.Texture != null
                    ? _atlas.Texture.width + "x" + _atlas.Texture.height + " produced, " + _atlas.Entries.Count + " entries"
                    : "NOT PRODUCED")
                : "disabled"));
            sb.AppendLine("gradient res: (n/a — BuildSprite is unusable; atlas raster size is the knob)");
            sb.AppendLine("flipY       : " + _flipY + "   preserveViewport: " + _preserveViewport);
            sb.AppendLine("tessellation: cord=" + _tess.MaxCordDeviation.ToString("0.###", CultureInfo.InvariantCulture)
                        + " tanAngle=" + _tess.MaxTanAngleDeviation.ToString("0.###", CultureInfo.InvariantCulture)
                        + " step=" + _tess.StepDistance.ToString("0.###", CultureInfo.InvariantCulture)
                        + " sampling=" + _tess.SamplingStepSize.ToString("0.###", CultureInfo.InvariantCulture));
            sb.AppendLine("geometry    : geoms=" + (_geoms?.Count ?? 0) + " verts=" + _vertexCount + " tris=" + _triangleCount);
            sb.AppendLine("mesh px     : " + _shapeWidthPx.ToString("0.#", CultureInfo.InvariantCulture)
                        + "x" + _shapeHeightPx.ToString("0.#", CultureInfo.InvariantCulture)
                        + "   (svg root says " + (_selected != null ? _selected.SizeLabel : "?") + ")");
            sb.AppendLine("timing ms   : import+tessellate=" + _lastImportMs.ToString("0.#", CultureInfo.InvariantCulture)
                        + " tessellate=" + _lastTessellateMs.ToString("0.#", CultureInfo.InvariantCulture));
            sb.AppendLine("shaders     :");
            if (_shaderAvailable == null) _shaderAvailable = ProbeShaders();
            foreach (string candidate in CandidateShaders)
            {
                bool found;
                bool ok = _shaderAvailable.TryGetValue(candidate, out found) && found;
                sb.AppendLine("              " + (ok ? "found   " : "MISSING ") + candidate);
            }
            if (!string.IsNullOrEmpty(_meshWarning)) sb.AppendLine("WARNING     : " + _meshWarning);
            if (!string.IsNullOrEmpty(_importError)) sb.AppendLine("ERROR       : " + _importError);
            sb.AppendLine("sheet       : " + _sheetIds.Count + " shape(s) queued");
            return sb.ToString();
        }

        static void EnsureDirectory(string assetPath)
        {
            string full = Path.GetFullPath(assetPath);
            if (!Directory.Exists(full)) Directory.CreateDirectory(full);
        }
    }
}
#endif
