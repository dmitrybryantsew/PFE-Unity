#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PFE.Editor.Importers;
using Unity.VectorGraphics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PFE.Editor.VectorSample
{
    /// <summary>
    /// Step 2 of the vector workstream: rebuild a whole <b>sprite</b> — not one shape — from the
    /// one-shot capture, and put it next to FFDec's own PNG export of that same sprite so the
    /// reconstruction can be judged rather than asserted.
    ///
    /// <para><b>Why this is a different window from <see cref="VectorSampleWindow"/>.</b> That one
    /// answers "does a single SVG import and tessellate, and how does the tessellation compare with a
    /// raster?" — one <c>DefineShape</c>, no composition. This one answers the question that actually
    /// decides the workstream: <i>a character is 26 parts re-assembled per frame by a display list;
    /// can the capture drive that assembly?</i> The parts come from the SVG export, the assembly comes
    /// from <c>VectorCapture.json</c>, and the ground truth is
    /// <c>pfe/sprites/DefineSprite_{id}_{name}/{frame}.png</c> — an artifact FFDec produced
    /// independently of our capture.</para>
    ///
    /// <para><b>Nothing here touches gameplay.</b> No scene asset, prefab, imported texture or runtime
    /// type is read or written. The preview is an off-screen scene of <c>HideFlags.HideAndDontSave</c>
    /// objects at a far world offset, destroyed on close.</para>
    /// </summary>
    public class VectorSpritePreviewWindow : EditorWindow
    {
        // ── Constants ────────────────────────────────────────────────────────

        const string CapturePath = "Assets/_PFE/Art/Vector/VectorCapture.json";

        /// <summary>Layer 31, as the other preview windows use. Isolation comes from the world offset.</summary>
        const int PreviewLayer = 31;

        /// <summary>Far enough that no other preview camera can frame it. Load-bearing: do not shrink.</summary>
        const float PreviewOffsetX = 500000f;

        const float RowHeight = 18f;

        // ── Capture + roots ──────────────────────────────────────────────────

        VectorCaptureData _data;
        VectorSpriteReconstructor _reconstructor;
        string _loadError;
        string _shapesRoot;
        string _spritesRoot;
        string _captureSummary = "not loaded";

        // ── Sprite list ──────────────────────────────────────────────────────

        readonly List<int> _allIds = new List<int>();
        readonly List<int> _filtered = new List<int>();
        string _filter = string.Empty;
        Vector2 _listScroll;
        int _selectedIndex = -1;
        int _selectedId = -1;

        // ── Frame ────────────────────────────────────────────────────────────

        int _frameIndex;

        // ── Build result ─────────────────────────────────────────────────────

        SpriteBuildResult _result;
        Mesh _mesh;
        Texture2D _reconTex;
        Material _material;
        string _shaderName = "(none)";

        // ── Reference PNG ────────────────────────────────────────────────────

        Texture2D _reference;
        string _referencePath;
        string _referenceError;
        string _spriteFolderName;

        // ── Preview scene ────────────────────────────────────────────────────

        GameObject _previewRoot;
        MeshFilter _meshFilter;
        MeshRenderer _meshRenderer;
        Camera _previewCamera;
        RenderTexture _previewRt;
        bool _sceneReady;
        int _rtSize = 512;
        float _zoom = 4f;
        Vector2 _pan;
        bool _dragging;

        // ── Settings ─────────────────────────────────────────────────────────

        ShapeOriginMode _originMode = ShapeOriginMode.UndoFfdecOrigin;
        bool _buildAtlas = true;
        bool _atlasOnlyWhenNeeded = true;
        uint _atlasRasterSize = 1024;
        bool _flipY = true;
        bool _preserveViewport = true;
        bool _showDiagnostics = true;
        int _reconScale = 2;

        // Cost guards. Defaults are chosen so that clicking any sprite in the list cannot freeze the
        // editor: the size guard withholds the 12 pathological full-screen single-path shapes, and
        // adaptive tolerance keeps the vertex count from scaling with the square of the shape size.
        int _maxShapeSvgKb = 128;
        bool _allowHeavyShapes;
        bool _adaptiveTolerance = true;

        // Off by default, and deliberately so. The fast-path draws ONE clamped quad where the real
        // shape is a repeating tile, so it changes the picture — which is precisely what this window
        // exists to detect. Enabling it trades fidelity for the ability to look at shapes that cannot
        // be tessellated at all (the 12 over-guard shapes, and the ~1.5 KB monsters the byte guard
        // cannot see). The diagnostics name every id drawn this way.
        bool _useRasterFastPath;
        int _rasterCostShapeCount;

        VectorUtils.TessellationOptions _tess = new VectorUtils.TessellationOptions
        {
            MaxCordDeviation = 0.5f,
            MaxTanAngleDeviation = 0.1f,
            StepDistance = 0.5f,
            SamplingStepSize = 0.2f,
        };

        Vector2 _mainScroll;
        string _status = string.Empty;
        bool _needsBuild;

        // ── Incremental build ────────────────────────────────────────────────
        //
        // A build is requested from the GUI but NEVER performed there. Two reasons, both learned the
        // hard way: a click lands on MouseUp, so building in the click handler blocks the editor inside
        // a GUI event (Unity shows "Hold on" naming the event, and there is no progress bar and no way
        // to cancel); and tessellating a sprite's whole display list is not cheap, because 12 of the
        // 2563 shapes are 128 KB - 1.9 MB single paths on 1920x1080 canvases.
        //
        // So: the GUI records a request, and EditorApplication.update tessellates a bounded slice per
        // editor frame with a progress bar and a Cancel button.

        sealed class PendingBuild
        {
            public int SpriteId;
            public int FrameIndex;
            public List<int> Shapes;
            public int Next;
            public long TotalBytes;
            public double StartedAt;
        }

        PendingBuild _pending;

        /// <summary>Cost of the current selection, measured on selection change so the row warns before the click.</summary>
        int _costShapeCount;
        long _costBytes;

        /// <summary>Milliseconds of tessellation allowed per editor frame. One shape is always allowed, so progress is guaranteed.</summary>
        const double SliceBudgetMs = 12.0;

        // ── Lifetime ─────────────────────────────────────────────────────────

        [MenuItem("PFE/Art/Vector Sprite Preview")]
        public static void ShowWindow()
        {
            var window = GetWindow<VectorSpritePreviewWindow>("Vector Sprite");
            window.minSize = new Vector2(1000, 640);
        }

        void OnEnable()
        {
            // Deliberately no build here: building allocates a hide-and-dont-save GameObject and
            // OnEnable runs on every domain reload. Load the (cheap, 229 ms) capture; the first build
            // is requested from the first OnGUI pass and then pumped off the GUI thread below.
            LoadCapture();
            EditorApplication.update += PumpBuild;
        }

        void OnDisable() { EditorApplication.update -= PumpBuild; Teardown(); }
        void OnDestroy() { EditorApplication.update -= PumpBuild; Teardown(); }

        void Teardown()
        {
            CleanupPreviewScene();
            ReleaseOutputs();
            ReleaseReference();
        }

        // ── Capture ──────────────────────────────────────────────────────────

        void LoadCapture()
        {
            _loadError = null;
            _data = null;
            _reconstructor = null;
            _allIds.Clear();

            if (!File.Exists(CapturePath))
            {
                _loadError = "Capture not found: " + CapturePath
                           + "\nRun .workbuddy-ai/tools/capture_vector_sprites.py to produce it.";
                _captureSummary = "missing";
                return;
            }

            try
            {
                _data = VectorCaptureReader.Parse(File.ReadAllText(CapturePath));
            }
            catch (Exception ex)
            {
                _loadError = "Capture parse failed: " + ex.GetType().Name + ": " + ex.Message;
                _captureSummary = "parse error";
                return;
            }

            _shapesRoot = ResolveShapesRoot();
            _spritesRoot = ResolveSpritesRoot();

            _reconstructor = new VectorSpriteReconstructor(_data, _shapesRoot)
            {
                Tessellation = _tess,
                BuildAtlas = _buildAtlas,
                AtlasRasterSize = _atlasRasterSize,
                FlipY = _flipY,
                PreserveViewport = _preserveViewport,
                OriginMode = _originMode,
            };

            _allIds.AddRange(_data.Sprites.Keys);
            _allIds.Sort();

            _captureSummary = string.Format(CultureInfo.InvariantCulture,
                "{0} sprites, {1} shapes, {2} frame instances  —  capture v{3}",
                _data.Sprites.Count, _data.Shapes.Count, GetCount("frameInstances"),
                _data.Manifest.CaptureVersion);

            ApplyFilter();

            // Land on a sprite that actually has several parts — a one-shape sprite cannot show
            // whether composition works, which is the whole question.
            int best = _filtered.FindIndex(id =>
                _data.Sprites[id].Frames.Length > 1 &&
                _data.Sprites[id].Frames.Any(f => f.Placements.Length >= 2));
            SelectIndex(best >= 0 ? best : 0, deferBuild: true);
        }

        double GetCount(string key)
            => _data != null && _data.Manifest.Counts.TryGetValue(key, out var v) ? v : 0;

        /// <summary>
        /// The SVG tree. Prefers the project's own resolution (<see cref="SourceImportPaths.ShapesRoot"/>,
        /// which honours <c>PFE_IMPORT_ROOT</c> and the saved setting) and falls back to the path the
        /// capture recorded — the capture may have been produced from a different checkout.
        /// </summary>
        string ResolveShapesRoot()
        {
            string project = SourceImportPaths.ShapesRoot;
            if (!string.IsNullOrEmpty(project) && Directory.Exists(project)) return project;

            string recorded = _data.Manifest.ShapesDir;
            if (!string.IsNullOrEmpty(recorded) && Directory.Exists(recorded)) return recorded;

            return project ?? recorded ?? string.Empty;
        }

        string ResolveSpritesRoot()
        {
            string project = SourceImportPaths.PfeSpritesRoot;
            if (!string.IsNullOrEmpty(project) && Directory.Exists(project)) return project;
            return project ?? string.Empty;
        }

        void ApplyFilter()
        {
            _filtered.Clear();
            string needle = (_filter ?? string.Empty).Trim();
            foreach (int id in _allIds)
            {
                if (needle.Length > 0 &&
                    id.ToString(CultureInfo.InvariantCulture).IndexOf(needle, StringComparison.Ordinal) < 0)
                    continue;
                _filtered.Add(id);
            }
        }

        void SelectIndex(int index, bool deferBuild = false)
        {
            if (_filtered.Count == 0) return;
            _selectedIndex = Mathf.Clamp(index, 0, _filtered.Count - 1);
            _selectedId = _filtered[_selectedIndex];
            _frameIndex = FirstDrawableFrame(_selectedId);
            LoadReference();

            // Never build here. SelectIndex is called from GUI.Button, which fires on MouseUp,
            // and doing the work inline freezes the editor inside a GUI event.
            if (deferBuild) _needsBuild = true;
            else RequestBuild();
        }

        /// <summary>
        /// The first frame that actually draws something. 88 sprites start with an empty frame
        /// (the SWF's first tag is <c>ShowFrame</c>), and more have a first frame of text/button
        /// placements only — landing on frame 0 for those reads as "this sprite is broken" when it is
        /// fine one frame later. Cheap: it resolves the display list but tessellates nothing.
        /// </summary>
        int FirstDrawableFrame(int spriteId)
        {
            if (_reconstructor == null) return 0;
            if (!_data.Sprites.TryGetValue(spriteId, out var sprite)) return 0;

            for (int i = 0; i < sprite.Frames.Length; i++)
                if (_reconstructor.DistinctShapeIds(spriteId, i).Count > 0) return i;
            return 0;
        }

        // ── Build ────────────────────────────────────────────────────────────

        void SyncSettings()
        {
            if (_reconstructor == null) return;
            _reconstructor.Tessellation = _tess;
            _reconstructor.BuildAtlas = _buildAtlas;
            _reconstructor.AtlasOnlyWhenNeeded = _atlasOnlyWhenNeeded;
            _reconstructor.AtlasRasterSize = _atlasRasterSize;
            _reconstructor.FlipY = _flipY;
            _reconstructor.PreserveViewport = _preserveViewport;
            _reconstructor.OriginMode = _originMode;
            _reconstructor.MaxShapeSvgBytes = Mathf.Max(1, _maxShapeSvgKb) * 1024L;
            _reconstructor.AllowHeavyShapes = _allowHeavyShapes;
            _reconstructor.AdaptiveTolerance = _adaptiveTolerance;
            _reconstructor.UseRasterFastPath = _useRasterFastPath;
        }

        /// <summary>
        /// Records a build request. Never does the work — see <see cref="PendingBuild"/> for why.
        /// </summary>
        void RequestBuild()
        {
            if (_reconstructor == null || _selectedId < 0)
            {
                _status = _loadError ?? "Nothing selected.";
                return;
            }

            SyncSettings();

            var shapes = _reconstructor.DistinctShapeIds(_selectedId, _frameIndex);
            long totalBytes = 0;
            foreach (int id in shapes) totalBytes += _reconstructor.GetShapeSvgBytes(id);
            _costShapeCount = shapes.Count;
            _costBytes = totalBytes;

            // Count what the BYTE guard cannot protect against. A bitmap-pattern fill is tiny on disk
            // and pathological to tessellate, so the cost line above under-reports it badly — and the
            // worst three offenders in the export (4652/495/1131, ~1.5 KB each) sail straight through
            // the guard. Shapes already over the guard are skipped here: they are withheld anyway, and
            // skipping them avoids re-reading the 1-2 MB SVGs just to label them.
            _rasterCostShapeCount = 0;
            if (!_useRasterFastPath)
            {
                long guard = Mathf.Max(1, _maxShapeSvgKb) * 1024L;
                foreach (int id in shapes)
                    if (_reconstructor.GetShapeSvgBytes(id) <= guard && _reconstructor.IsBitmapPatternShape(id))
                        _rasterCostShapeCount++;
            }

            _pending = new PendingBuild
            {
                SpriteId = _selectedId,
                FrameIndex = _frameIndex,
                Shapes = shapes,
                Next = 0,
                TotalBytes = totalBytes,
                StartedAt = EditorApplication.timeSinceStartup,
            };
            Repaint();
        }

        void CancelBuild()
        {
            if (_pending == null) return;
            _status = "Build cancelled after " + _pending.Next + " of " + _pending.Shapes.Count + " shapes.";
            _pending = null;
            Repaint();
        }

        /// <summary>
        /// Tessellates a bounded slice of the pending build, once per editor frame. Runs on
        /// <c>EditorApplication.update</c>, i.e. outside the GUI event, which is the whole point.
        /// </summary>
        void PumpBuild()
        {
            var p = _pending;
            if (p == null || _reconstructor == null) return;

            var slice = Stopwatch.StartNew();
            int done = 0;

            // At least one shape per slice, so a single expensive shape still makes progress rather
            // than spinning forever below the budget.
            while (p.Next < p.Shapes.Count && (done == 0 || slice.Elapsed.TotalMilliseconds < SliceBudgetMs))
            {
                _reconstructor.PreloadShape(p.Shapes[p.Next]);
                p.Next++;
                done++;
            }

            if (p.Next >= p.Shapes.Count)
            {
                _pending = null;
                FinishBuild(p);
            }

            Repaint();
        }

        /// <summary>Everything after the shape cache is warm: transform, atlas, mesh, preview.</summary>
        void FinishBuild(PendingBuild p)
        {
            ReleaseOutputs();

            _result = _reconstructor.BuildFrame(p.SpriteId, p.FrameIndex);

            if (_result.Mesh != null)
            {
                _mesh = _result.Mesh;
                BuildMaterial();
                SetupPreviewScene();
                FitToView();
                RenderPreview();
                BuildReconstructionTexture();
            }

            double wall = EditorApplication.timeSinceStartup - p.StartedAt;
            _status = Describe(_result) + string.Format(CultureInfo.InvariantCulture,
                "   [{0:0.0} s wall, {1} shapes, {2:0.#} MB of SVG]",
                wall, p.Shapes.Count, p.TotalBytes / 1048576.0);
        }

        string Describe(SpriteBuildResult r)
        {
            if (r == null) return "no result";
            if (r.Mesh == null) return "sym" + r.SpriteId + " frame " + r.FrameIndex + " — " + (r.Error ?? "failed");

            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "sym{0} frame {1}: {2} shapes → {3} verts, {4} tris   ({5:0} ms build, {6:0} ms tessellate)",
                r.SpriteId, r.FrameIndex, r.ResolvedShapeCount, r.VertexCount, r.TriangleCount,
                r.BuildMs, r.TessellateMs);
            if (r.SkippedIds.Count > 0)
                sb.Append("   skipped ").Append(r.SkippedIds.Count).Append(" geometry-less placement(s): ")
                  .Append(string.Join(", ", r.SkippedIds.Take(8).Select(i => i.ToString(CultureInfo.InvariantCulture))));
            if (r.MissingSvgIds.Count > 0)
                sb.Append("   MISSING SVG: ").Append(string.Join(", ", r.MissingSvgIds.Select(i => i.ToString(CultureInfo.InvariantCulture))));
            if (r.SkippedHeavy.Count > 0)
                sb.Append("   withheld ").Append(r.SkippedHeavy.Count).Append(" heavy shape(s)");
            if (r.RasterApproximatedIds.Count > 0)
                sb.Append("   ").Append(r.RasterApproximatedIds.Count).Append(" shape(s) drawn via RASTER approximation");
            return sb.ToString();
        }

        // ── Reference PNG ────────────────────────────────────────────────────

        /// <summary>
        /// Finds FFDec's PNG folder for the selected sprite and loads the current frame.
        ///
        /// <para>Loaded through <c>Texture2D.LoadImage</c> from raw bytes rather than the
        /// AssetDatabase, because the source tree is outside <c>Assets/</c> — the AssetDatabase cannot
        /// see it at all, and a silent null there would read as "no reference available" instead of
        /// "wrong loader".</para>
        /// </summary>
        void LoadReference()
        {
            ReleaseReference();
            _spriteFolderName = null;

            if (string.IsNullOrEmpty(_spritesRoot) || _selectedId < 0)
            {
                _referenceError = "sprites root not resolved";
                return;
            }

            string prefix = "DefineSprite_" + _selectedId.ToString(CultureInfo.InvariantCulture);
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(_spritesRoot, prefix + "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                _referenceError = "enumerate failed: " + ex.Message;
                return;
            }

            if (dirs.Length == 0)
            {
                _referenceError = "no PNG folder " + prefix + "*";
                return;
            }

            _spriteFolderName = Path.GetFileName(dirs[0]);
            string png = Path.Combine(dirs[0], (_frameIndex + 1).ToString(CultureInfo.InvariantCulture) + ".png");
            if (!File.Exists(png))
            {
                _referenceError = "no " + (_frameIndex + 1) + ".png in " + _spriteFolderName;
                return;
            }

            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                if (!tex.LoadImage(File.ReadAllBytes(png)))
                {
                    DestroyImmediate(tex);
                    _referenceError = "LoadImage refused " + Path.GetFileName(png);
                    return;
                }
                _reference = tex;
                _referencePath = png;
                _referenceError = null;
            }
            catch (Exception ex)
            {
                _referenceError = "load failed: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        void ReleaseReference()
        {
            if (_reference != null) DestroyImmediate(_reference);
            _reference = null;
            _referencePath = null;
        }

        // ── Reconstruction texture ───────────────────────────────────────────

        /// <summary>
        /// Renders the built mesh flat into a texture sized to the sprite's own bounds, so it can be
        /// laid beside the reference PNG at matched scale. Uses the preview camera — the same path the
        /// interactive view uses, so the comparison cannot disagree with what is on screen.
        /// </summary>
        void BuildReconstructionTexture()
        {
            if (_reconTex != null) { DestroyImmediate(_reconTex); _reconTex = null; }
            if (_previewCamera == null || _mesh == null || _mesh.vertexCount == 0) return;

            var size = _mesh.bounds.size;                     // world units
            float wPx = size.x * VectorSpriteReconstructor.SvgPixelsPerUnit;
            float hPx = size.y * VectorSpriteReconstructor.SvgPixelsPerUnit;
            if (wPx <= 0f || hPx <= 0f) return;

            int w = Mathf.Max(1, Mathf.RoundToInt(wPx * _reconScale));
            int h = Mathf.Max(1, Mathf.RoundToInt(hPx * _reconScale));

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

            try
            {
                var center = _mesh.bounds.center;
                _previewCamera.orthographicSize = size.y * 0.5f;
                _previewCamera.transform.position = new Vector3(PreviewOffsetX + center.x, center.y, -10f);
                _previewCamera.targetTexture = rt;
                _previewCamera.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
                tex.Apply();
                tex.hideFlags = HideFlags.HideAndDontSave;
                _reconTex = tex;
            }
            catch (Exception ex)
            {
                _status = "reconstruction bake failed: " + ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                _previewCamera.targetTexture = savedTarget;
                _previewCamera.orthographicSize = savedSize;
                _previewCamera.transform.position = savedPos;
                RenderTexture.active = savedActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // ── Material ─────────────────────────────────────────────────────────

        static readonly string[] CandidateShaders =
        {
            "PFE/Vector Sample/VertexColor",
            "PFE/Vector Sample/FlatFill",
            "Universal Render Pipeline/2D/Mesh2D-Unlit-Default",
            "Unlit/Vector",
        };

        const string OurVertexColorShader = "PFE/Vector Sample/VertexColor";
        const string OurFlatFillShader = "PFE/Vector Sample/FlatFill";
        const string VertexColorAssetName = "PFEVectorSampleVertexColor";
        const string FlatFillAssetName = "PFEVectorSampleFlatFill";

        /// <summary>
        /// Resolves a shader, preferring an asset load for the two that live under <c>Editor/</c> —
        /// <c>Shader.Find</c> is least reliable exactly there, because Editor-folder assets are
        /// excluded from builds.
        /// </summary>
        static Shader FindShader(string shaderName)
        {
            string assetName = null;
            if (shaderName == OurVertexColorShader) assetName = VertexColorAssetName;
            else if (shaderName == OurFlatFillShader) assetName = FlatFillAssetName;

            if (assetName != null)
            {
                foreach (string guid in AssetDatabase.FindAssets(assetName + " t:Shader"))
                {
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
                    if (shader != null) return shader;
                }
            }
            return Shader.Find(shaderName);
        }

        /// <summary>
        /// Picks the shader for the current path. The atlas carries the fills in <c>_MainTex</c> and
        /// the UVs index it; without an atlas the fills live in vertex colour, which the stock URP 2D
        /// mesh shader cannot read at all (its vertex input declares no <c>COLOR</c>). So the choice
        /// follows the atlas switch rather than being a free preference.
        /// </summary>
        void BuildMaterial()
        {
            // Follow the atlas that actually exists, not the atlas switch: the reconstructor skips the
            // atlas when every fill is a solid colour, and picking the URP atlas shader in that case
            // would sample a null _MainTex and draw nothing.
            bool haveAtlas = _result != null && _result.Atlas != null && _result.Atlas.Texture != null;

            string wanted = haveAtlas
                ? "Universal Render Pipeline/2D/Mesh2D-Unlit-Default"
                : OurVertexColorShader;

            var shader = FindShader(wanted);
            if (shader == null)
            {
                var fallback = FindShader(OurVertexColorShader);
                _shaderName = wanted + "  ⚠ NOT FOUND"
                            + (fallback != null ? " → " + OurVertexColorShader : " (and no fallback)");
                shader = fallback;
            }
            else
            {
                _shaderName = wanted;
            }

            if (shader == null) { _material = null; return; }

            if (_material != null) DestroyImmediate(_material);
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            // The atlas is what carries the fills on the atlas path, so the material has to point at
            // it — without this the stock URP shader samples a null _MainTex and draws nothing.
            if (_result != null && _result.Atlas != null && _result.Atlas.Texture != null &&
                _material.HasProperty("_MainTex"))
            {
                _material.SetTexture("_MainTex", _result.Atlas.Texture);
            }
            if (_material.HasProperty("_White"))
                _material.SetColor("_White", Color.white);
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

            CleanupPreviewScene();
            if (_mesh == null) return;

            _previewRoot = new GameObject("[VectorSprite_Root]")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer,
            };
            _previewRoot.transform.position = new Vector3(PreviewOffsetX, 0f, 0f);

            var rendererGo = new GameObject("sprite_mesh")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer,
            };
            rendererGo.transform.SetParent(_previewRoot.transform);
            rendererGo.transform.localPosition = Vector3.zero;

            _meshFilter = rendererGo.AddComponent<MeshFilter>();
            _meshFilter.sharedMesh = _mesh;

            _meshRenderer = rendererGo.AddComponent<MeshRenderer>();
            _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;
            if (_material != null) _meshRenderer.sharedMaterial = _material;

            var camGo = new GameObject("[VectorSprite_Camera]")
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
            _previewRt = new RenderTexture(_rtSize, _rtSize, 16) { hideFlags = HideFlags.HideAndDontSave };
        }

        void CleanupPreviewScene()
        {
            if (_previewRoot != null) DestroyImmediate(_previewRoot);
            if (_previewCamera != null) DestroyImmediate(_previewCamera.gameObject);
            if (_previewRt != null) DestroyImmediate(_previewRt);

            _previewRoot = null;
            _meshFilter = null;
            _meshRenderer = null;
            _previewCamera = null;
            _previewRt = null;
            _sceneReady = false;
        }

        void ReleaseOutputs()
        {
            if (_mesh != null) { DestroyImmediate(_mesh); _mesh = null; }
            if (_material != null) { DestroyImmediate(_material); _material = null; }
            if (_reconTex != null) { DestroyImmediate(_reconTex); _reconTex = null; }
            _result = null;
        }

        bool HasGeometry => _mesh != null && _mesh.vertexCount > 0;

        float ZoomToOrthoSize()
            => _rtSize / (2f * VectorSpriteReconstructor.SvgPixelsPerUnit * Mathf.Max(0.01f, _zoom));

        void FitToView()
        {
            if (_mesh == null || !HasGeometry) { _zoom = 4f; _pan = Vector2.zero; return; }
            var size = _mesh.bounds.size;
            float wPx = size.x * VectorSpriteReconstructor.SvgPixelsPerUnit;
            float hPx = size.y * VectorSpriteReconstructor.SvgPixelsPerUnit;
            if (wPx <= 0f || hPx <= 0f) { _zoom = 4f; return; }
            _zoom = Mathf.Clamp(Mathf.Min(_rtSize / wPx, _rtSize / hPx), 0.2f, 64f);
            _pan = Vector2.zero;
        }

        void RenderPreview()
        {
            if (!_sceneReady || _previewCamera == null || _previewRt == null || !HasGeometry) return;
            if (_previewRt.width != _rtSize) RecreateRenderTexture();

            var center = _mesh.bounds.center;
            _previewCamera.orthographicSize = ZoomToOrthoSize();
            _previewCamera.transform.position = new Vector3(
                PreviewOffsetX + center.x + _pan.x, center.y + _pan.y, -10f);

            _previewCamera.targetTexture = _previewRt;
            _previewCamera.Render();
            _previewCamera.targetTexture = null;
        }

        // ── GUI ──────────────────────────────────────────────────────────────

        void OnGUI()
        {
            if (_needsBuild && _selectedId >= 0)
            {
                _needsBuild = false;
                RequestBuild();
            }

            _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);

            DrawHeader();

            if (_data == null)
            {
                EditorGUILayout.HelpBox(_loadError ?? "Capture not loaded.", MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.26f));
            DrawSpriteList();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.32f));
            DrawPreviewPanel();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical();
            DrawSettingsPanel();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            DrawComparisonPanel();

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_status, _result != null && _result.Mesh == null && _result.Error != null
                    ? MessageType.Error : MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawHeader()
        {
            EditorGUILayout.LabelField("AS3 Vector Sprite Reconstruction Preview", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("capture", _captureSummary, EditorStyles.miniLabel);
            EditorGUILayout.LabelField("shapes root", _shapesRoot ?? "(unresolved)", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("sprites root", _spritesRoot ?? "(unresolved)", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _filter = EditorGUILayout.TextField("Filter id", _filter, GUILayout.Width(200));
            if (EditorGUI.EndChangeCheck())
            {
                ApplyFilter();
                if (_filtered.Count > 0) SelectIndex(0);
            }
            if (GUILayout.Button("Reload capture", GUILayout.Width(120))) LoadCapture();
            if (GUILayout.Button("Clear shape cache", GUILayout.Width(140)))
            {
                _reconstructor?.ClearCache();
                _status = "Shape geometry cache cleared.";
            }
            if (_reconstructor != null)
                EditorGUILayout.LabelField(_reconstructor.CachedShapeCount + " shapes cached, "
                    + _reconstructor.CacheErrorCount + " with errors"
                    + (_reconstructor.HeavySkippedCount > 0
                        ? ", " + _reconstructor.HeavySkippedCount + " over the size guard"
                        : ""),
                    EditorStyles.miniLabel, GUILayout.Width(300));
            EditorGUILayout.EndHorizontal();

            if (_pending != null) DrawBuildProgress();
        }

        /// <summary>
        /// The progress bar for an in-flight build. Shown in the header so it is visible whichever panel
        /// the user is looking at, and it carries the Cancel button — a build that cannot be stopped is
        /// what made the earlier freeze unrecoverable.
        /// </summary>
        void DrawBuildProgress()
        {
            var p = _pending;
            int total = Mathf.Max(1, p.Shapes.Count);
            double elapsed = EditorApplication.timeSinceStartup - p.StartedAt;

            EditorGUILayout.BeginHorizontal();
            var rect = GUILayoutUtility.GetRect(0f, 18f, GUILayout.ExpandWidth(true));
            EditorGUI.ProgressBar(rect,
                p.Next / (float)total,
                string.Format(CultureInfo.InvariantCulture,
                    "tessellating {0}/{1} shapes  ·  {2:0.#} MB of SVG  ·  {3:0.0} s",
                    p.Next, p.Shapes.Count, p.TotalBytes / 1048576.0, elapsed));
            if (GUILayout.Button("Cancel", GUILayout.Width(70))) CancelBuild();
            EditorGUILayout.EndHorizontal();
        }

        void DrawSpriteList()
        {
            EditorGUILayout.LabelField("Sprites", EditorStyles.boldLabel);
            _listScroll = EditorGUILayout.BeginScrollView(_listScroll, GUILayout.ExpandHeight(true));

            // Virtualised: an unfiltered list is 1653 rows.
            int visible = Mathf.CeilToInt(position.height / RowHeight) + 2;
            int first = Mathf.Max(0, Mathf.FloorToInt(_listScroll.y / RowHeight));
            int last = Mathf.Min(_filtered.Count - 1, first + visible);

            if (first > 0) GUILayout.Space(first * RowHeight);
            for (int i = first; i <= last; i++) DrawSpriteRow(i);
            if (last < _filtered.Count - 1) GUILayout.Space((_filtered.Count - 1 - last) * RowHeight);

            EditorGUILayout.EndScrollView();
        }

        void DrawSpriteRow(int i)
        {
            int id = _filtered[i];
            var sprite = _data.Sprites[id];
            var rect = GUILayoutUtility.GetRect(0f, RowHeight, GUILayout.ExpandWidth(true));

            if (Event.current.type == EventType.Repaint)
            {
                if (i == _selectedIndex) EditorGUI.DrawRect(rect, new Color(0.24f, 0.48f, 0.90f, 0.35f));
                else if ((i & 1) == 0) EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.03f));
            }

            if (GUI.Button(rect, GUIContent.none, GUIStyle.none)) SelectIndex(i);

            int maxParts = 0;
            foreach (var f in sprite.Frames)
                if (f.Placements.Length > maxParts) maxParts = f.Placements.Length;

            GUI.Label(rect, string.Format(CultureInfo.InvariantCulture,
                "sym{0}   {1}f   max {2} parts", id, sprite.FrameCount, maxParts), EditorStyles.miniLabel);
        }

        void DrawPreviewPanel()
        {
            EditorGUILayout.LabelField("Reconstruction", EditorStyles.boldLabel);

            if (_sceneReady && _previewRt != null)
            {
                float avail = position.width * 0.30f;
                float size = Mathf.Min(avail, _rtSize);
                var rect = GUILayoutUtility.GetRect(size, size);
                EditorGUI.DrawPreviewTexture(rect, _previewRt, null, ScaleMode.ScaleToFit);

                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Pan);
                var evt = Event.current;
                if (evt.type == EventType.MouseDown && evt.button == 0 && rect.Contains(evt.mousePosition))
                {
                    _dragging = true; evt.Use();
                }
                else if (evt.type == EventType.MouseUp && evt.button == 0 && _dragging)
                {
                    _dragging = false; evt.Use();
                }
                else if (evt.type == EventType.MouseLeaveWindow && _dragging)
                {
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
                GUI.Label(rect, _result?.Error ?? "Nothing built", EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Zoom (px per SVG px)", GUILayout.Width(140));
            _zoom = Mathf.Exp(EditorGUILayout.Slider(Mathf.Log(_zoom), Mathf.Log(0.2f), Mathf.Log(64f)));
            EditorGUILayout.LabelField(_zoom.ToString("0.##", CultureInfo.InvariantCulture) + "x", GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Fit", GUILayout.Width(40))) FitToView();
            if (GUILayout.Button("1x", GUILayout.Width(40))) { _zoom = 1f; _pan = Vector2.zero; }
            if (GUILayout.Button("4x", GUILayout.Width(40))) { _zoom = 4f; _pan = Vector2.zero; }
            EditorGUILayout.LabelField("Render size", GUILayout.Width(70));
            _rtSize = EditorGUILayout.IntSlider(_rtSize, 128, 1024);
            EditorGUILayout.EndHorizontal();
            if (EditorGUI.EndChangeCheck()) RenderPreview();
        }

        void DrawSettingsPanel()
        {
            EditorGUILayout.LabelField("Frame", EditorStyles.boldLabel);

            if (_selectedId >= 0 && _data.Sprites.TryGetValue(_selectedId, out var sprite))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("frame", GUILayout.Width(40));
                int newFrame = EditorGUILayout.IntSlider(_frameIndex, 0, Mathf.Max(0, sprite.Frames.Length - 1));
                EditorGUILayout.LabelField("/ " + sprite.Frames.Length, GUILayout.Width(50));
                EditorGUILayout.EndHorizontal();

                if (newFrame != _frameIndex)
                {
                    _frameIndex = newFrame;
                    LoadReference();
                    RequestBuild();
                }

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("◀", GUILayout.Width(28)))
                {
                    _frameIndex = Mathf.Max(0, _frameIndex - 1); LoadReference(); RequestBuild();
                }
                if (GUILayout.Button("▶", GUILayout.Width(28)))
                {
                    _frameIndex = Mathf.Min(sprite.Frames.Length - 1, _frameIndex + 1); LoadReference(); RequestBuild();
                }
                if (GUILayout.Button("Rebuild", GUILayout.Width(80))) RequestBuild();
                if (GUILayout.Button("Export reconstruction PNG", GUILayout.Width(180))) ExportReconstruction();
                EditorGUILayout.EndHorizontal();

                // Measured before the build, so an expensive sprite is visible as a number rather than
                // as a frozen editor.
                bool heavy = _costBytes > Mathf.Max(1, _maxShapeSvgKb) * 1024L;
                EditorGUILayout.LabelField("cost",
                    string.Format(CultureInfo.InvariantCulture, "{0} shapes, {1:0.##} MB of SVG{2}",
                        _costShapeCount, _costBytes / 1048576.0,
                        heavy && !_allowHeavyShapes ? "  — over the guard, some shapes will be withheld" : ""),
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Pipeline", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            _originMode = (ShapeOriginMode)EditorGUILayout.EnumPopup("Shape origin", _originMode);
            EditorGUILayout.HelpBox(OriginModeHint(_originMode), MessageType.None);

            _buildAtlas = EditorGUILayout.ToggleLeft("Build atlas (gradients + bitmap fills)", _buildAtlas);
            EditorGUI.BeginDisabledGroup(!_buildAtlas);
            _atlasRasterSize = (uint)EditorGUILayout.IntPopup("Atlas raster size", (int)_atlasRasterSize,
                new[] { "256", "512", "1024", "2048" }, new[] { 256, 512, 1024, 2048 });
            EditorGUI.EndDisabledGroup();
            _atlasOnlyWhenNeeded = EditorGUILayout.ToggleLeft(
                "…only when a fill needs it (solid fills skip the atlas)", _atlasOnlyWhenNeeded);

            _flipY = EditorGUILayout.ToggleLeft("Flip Y axis (SVG is Y-down)", _flipY);
            _preserveViewport = EditorGUILayout.ToggleLeft("Preserve viewport", _preserveViewport);

            EditorGUILayout.LabelField("shader", _shaderName, EditorStyles.miniLabel);
            EditorGUILayout.LabelField("recon bake scale", "1 px SVG = " + _reconScale + " px texture", EditorStyles.miniLabel);
            _reconScale = EditorGUILayout.IntSlider(_reconScale, 1, 8);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Cost guards", EditorStyles.miniLabel);
            EditorGUI.indentLevel++;
            _maxShapeSvgKb = EditorGUILayout.IntSlider("Max SVG size (KB)", _maxShapeSvgKb, 16, 4096);
            EditorGUILayout.HelpBox(
                "12 of the 2563 shapes are 128 KB - 1.9 MB: a single path with ~150 000-180 000 commands on "
                + "a 1920x1080 canvas (visMainMenu, visualWait). Tessellating one of those at a 0.5 px chord "
                + "tolerance is what froze the editor. Shapes over this limit are withheld and listed below.",
                MessageType.None);
            _allowHeavyShapes = EditorGUILayout.ToggleLeft(
                "Allow heavy shapes (expect a multi-second, possibly minute-long freeze)", _allowHeavyShapes);
            _adaptiveTolerance = EditorGUILayout.ToggleLeft(
                "Adaptive tolerance (scale with shape size)", _adaptiveTolerance);

            _useRasterFastPath = EditorGUILayout.ToggleLeft(
                "Raster fast-path for bitmap-pattern fills (APPROXIMATE: one quad, not the tiled fill)",
                _useRasterFastPath);
            if (_useRasterFastPath)
                EditorGUILayout.HelpBox(
                    "Bitmap-pattern shapes are drawn as a single clamped quad from their source bitmap in "
                    + "_assets/images/ instead of tessellating the repeating tile. This is the only way to "
                    + "see shapes that otherwise cannot be tessellated at all, but it is NOT a faithful "
                    + "reconstruction — the diagnostics list every id drawn this way.",
                    MessageType.Warning);
            else if (_rasterCostShapeCount > 0)
                EditorGUILayout.HelpBox(
                    _rasterCostShapeCount + " of the " + _costShapeCount + " shapes in this frame are "
                    + "bitmap-pattern fills that the byte guard CANNOT withhold (they are small on disk "
                    + "and pathological to tessellate — 4652/495/1131 are ~1.5 KB and take 12-22 min). "
                    + "All 12 shapes over the guard are also in this class. Without the fast-path above, "
                    + "selecting this sprite can freeze the editor.",
                    MessageType.Warning);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Tessellation", EditorStyles.miniLabel);
            EditorGUI.indentLevel++;
            _tess.MaxCordDeviation = EditorGUILayout.Slider("Max cord deviation", _tess.MaxCordDeviation, 0f, 4f);
            _tess.MaxTanAngleDeviation = EditorGUILayout.Slider("Max tangent angle", _tess.MaxTanAngleDeviation, 0f, 45f);
            _tess.StepDistance = EditorGUILayout.Slider("Step distance", _tess.StepDistance, 0f, 8f);
            _tess.SamplingStepSize = EditorGUILayout.Slider("Sampling step size", _tess.SamplingStepSize, 0f, 1f);
            if (_adaptiveTolerance)
                EditorGUILayout.LabelField(
                    "effective: x" + Mathf.Max(1f, 1920f / 256f).ToString("0.#", CultureInfo.InvariantCulture)
                    + " at 1920 px", EditorStyles.miniLabel);
            EditorGUI.indentLevel--;

            if (EditorGUI.EndChangeCheck())
            {
                // Tessellation changes invalidate the cached geometry; the others do not, but a full
                // rebuild is cheap next to being subtly stale. A guard change must also drop the
                // withheld shapes, or raising the limit would have no effect until the cache is cleared.
                _reconstructor?.ClearCache();
                RequestBuild();
            }

            EditorGUILayout.Space(4);
            _showDiagnostics = EditorGUILayout.Foldout(_showDiagnostics, "Diagnostics", true);
            if (_showDiagnostics) DrawDiagnostics();
        }

        static string OriginModeHint(ShapeOriginMode mode)
        {
            switch (mode)
            {
                case ShapeOriginMode.RawSvgSpace:
                    return "Use tessellated vertices as-is. Correct if TessellateScene does NOT bake the "
                         + "root <g>. If it does, every shape is offset by its own bounds origin — most "
                         + "visible on multi-part sprites.";
                case ShapeOriginMode.AlignGeometryBounds:
                    return "Translate each shape so its own geometry bbox lands on its SWF bounds. Same "
                         + "as Undo FFDec origin when the SVG is unpadded; measures instead of trusting.";
                default:
                    return "Undo FFDec's re-origin with v += (Xmin,Ymin)/20 from the capture. Exact for "
                         + "the 2357 unpadded shapes, ≤ margin/2 out otherwise.";
            }
        }

        void DrawDiagnostics()
        {
            EditorGUI.indentLevel++;
            if (_result == null)
            {
                EditorGUILayout.LabelField("(no build yet)", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("sprite / frame",
                    _result.SpriteId + " / " + _result.FrameIndex, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("resolved shapes", _result.ResolvedShapeCount.ToString(CultureInfo.InvariantCulture), EditorStyles.miniLabel);
                EditorGUILayout.LabelField("verts / tris",
                    _result.VertexCount + " / " + _result.TriangleCount, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("build / tessellate ms",
                    _result.BuildMs.ToString("0.#", CultureInfo.InvariantCulture) + " / "
                    + _result.TessellateMs.ToString("0.#", CultureInfo.InvariantCulture), EditorStyles.miniLabel);
                EditorGUILayout.LabelField("atlas",
                    _result.Atlas != null && _result.Atlas.Texture != null
                        ? _result.Atlas.Texture.width + "x" + _result.Atlas.Texture.height
                          + "  (" + _result.Atlas.Entries.Count + " entries)"
                        : _result.AtlasSkippedAsUnnecessary
                            ? "skipped — every fill is a solid colour"
                            : "(none)",
                    EditorStyles.miniLabel);
                if (_result.RasterApproximatedIds.Count > 0)
                {
                    EditorGUILayout.LabelField("raster-approximated shapes",
                        string.Join(", ", _result.RasterApproximatedIds.Take(20)
                            .Select(i => i.ToString(CultureInfo.InvariantCulture))),
                        EditorStyles.miniLabel);
                    EditorGUILayout.HelpBox(
                        "The shapes above were drawn as a single clamped quad from their source bitmap, "
                        + "not tessellated. The picture is NOT faithful for them — turn the raster "
                        + "fast-path off to compare against the true (slow) reconstruction.",
                        MessageType.Warning);
                }

                EditorGUILayout.LabelField("mesh bounds px",
                    _result.MeshBounds.size.x.ToString("0.#", CultureInfo.InvariantCulture) + "x"
                    + _result.MeshBounds.size.y.ToString("0.#", CultureInfo.InvariantCulture), EditorStyles.miniLabel);

                if (_result.HasSwfBounds)
                    EditorGUILayout.LabelField("swf bounds px",
                        (_result.SwfMax.x - _result.SwfMin.x).ToString("0.#", CultureInfo.InvariantCulture) + "x"
                        + (_result.SwfMax.y - _result.SwfMin.y).ToString("0.#", CultureInfo.InvariantCulture),
                        EditorStyles.miniLabel);

                // The most useful single number here: the reconstruction and FFDec's PNG should be the
                // same size. A mismatch means the origin mode is wrong, not that the art is wrong.
                if (_reference != null)
                {
                    EditorGUILayout.LabelField("reference PNG px",
                        _reference.width + "x" + _reference.height, EditorStyles.miniLabel);
                    var r = _result.MeshBounds.size;
                    float rw = r.x * VectorSpriteReconstructor.SvgPixelsPerUnit;
                    float rh = r.y * VectorSpriteReconstructor.SvgPixelsPerUnit;
                    float arRecon = rh > 0f ? rw / rh : 0f;
                    float arRef = _reference.height > 0 ? _reference.width / (float)_reference.height : 0f;
                    EditorGUILayout.LabelField("aspect recon vs png",
                        arRecon.ToString("0.###", CultureInfo.InvariantCulture) + "  vs  "
                        + arRef.ToString("0.###", CultureInfo.InvariantCulture),
                        EditorStyles.miniLabel);
                }

                if (_result.SkippedIds.Count > 0)
                    EditorGUILayout.LabelField("skipped ids",
                        string.Join(", ", _result.SkippedIds.Take(20).Select(i => i.ToString(CultureInfo.InvariantCulture))),
                        EditorStyles.miniLabel);
                if (_result.MissingSvgIds.Count > 0)
                    EditorGUILayout.LabelField("missing SVG ids",
                        string.Join(", ", _result.MissingSvgIds.Select(i => i.ToString(CultureInfo.InvariantCulture))),
                        EditorStyles.miniLabel);

                // Withheld shapes are the reason a preview can look incomplete, so they are named with
                // their size rather than just counted — and the remedy is stated, because it is a
                // toggle and not a missing file.
                if (_result.SkippedHeavy.Count > 0)
                {
                    EditorGUILayout.LabelField("withheld (heavy) shapes", EditorStyles.miniLabel);
                    foreach (var s in _result.SkippedHeavy)
                        EditorGUILayout.LabelField("   shape " + s.Id,
                            string.Format(CultureInfo.InvariantCulture, "{0:0.#} KB — {1}",
                                s.Bytes / 1024.0, s.Reason), EditorStyles.miniLabel);
                    EditorGUILayout.HelpBox(
                        "This preview is incomplete: the shapes above are withheld by the size guard. "
                        + "Raise \"Max SVG size\" or tick \"Allow heavy shapes\" to include them — the "
                        + "editor may freeze for a while.", MessageType.Warning);
                }
            }
            EditorGUI.indentLevel--;
        }

        void DrawComparisonPanel()
        {
            EditorGUILayout.LabelField("Original FFDec PNG  vs  reconstruction", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                _referencePath ?? (_referenceError ?? "(no reference)"), EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            DrawSlot("FFDec PNG (ground truth)", _reference, _referenceError);
            DrawSlot("reconstruction (" + _reconScale + "x)", _reconTex, _result?.Error);
            EditorGUILayout.EndHorizontal();
        }

        void DrawSlot(string caption, Texture2D tex, string error)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(300));
            EditorGUILayout.LabelField(caption, EditorStyles.miniLabel);
            var rect = GUILayoutUtility.GetRect(290f, 290f);
            if (tex != null)
            {
                EditorGUI.DrawPreviewTexture(rect, tex, null, ScaleMode.ScaleToFit);
                EditorGUILayout.LabelField(tex.width + "x" + tex.height, EditorStyles.miniLabel);
            }
            else
            {
                EditorGUI.DrawRect(rect, new Color(0.22f, 0.22f, 0.22f));
                GUI.Label(rect, error ?? "—", EditorStyles.centeredGreyMiniLabel);
                EditorGUILayout.LabelField(" ", EditorStyles.miniLabel);
            }
            EditorGUILayout.EndVertical();
        }

        // ── Export ───────────────────────────────────────────────────────────

        void ExportReconstruction()
        {
            if (_reconTex == null)
            {
                _status = "Nothing to export.";
                return;
            }

            string dir = "Assets/_PFE/Art/VectorSamples";
            string full = Path.GetFullPath(dir);
            if (!Directory.Exists(full)) Directory.CreateDirectory(full);

            string name = "recon_sym" + _selectedId.ToString(CultureInfo.InvariantCulture)
                        + "_f" + _frameIndex.ToString(CultureInfo.InvariantCulture)
                        + "_" + _originMode + ".png";
            string path = Path.Combine(dir, name);

            try
            {
                File.WriteAllBytes(Path.GetFullPath(path), _reconTex.EncodeToPNG());
                AssetDatabase.Refresh();
                _status = "Exported " + path;
            }
            catch (Exception ex)
            {
                _status = "Export failed: " + ex.GetType().Name + ": " + ex.Message;
            }
        }
    }
}
#endif
