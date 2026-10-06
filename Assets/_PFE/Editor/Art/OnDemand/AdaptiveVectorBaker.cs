#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using PFE.Editor.Importers;
using PFE.Editor.VectorSample;
using Unity.VectorGraphics;
using UnityEngine;
using UnityEngine.Rendering;

namespace PFE.Editor.Art.OnDemand
{
    /// <summary>
    /// Bakes genuine vector shapes (characters, icons, procedural shapes) using adaptive tolerance
    /// to avoid geometric subdivision blowup, with optional atlas skipping for solid fills.
    /// </summary>
    public sealed class AdaptiveVectorBaker
    {
        readonly string _shapesRoot;

        public float BaseChordDeviation = 0.5f;
        public float BaseStepDistance = 0.5f;
        public float BaseSamplingStepSize = 0.2f;
        public float BaseTanAngleDeviation = 0.1f;
        public float ReferenceSizePx = 256f;

        public uint AtlasRasterSize = 1024;
        public float SvgPixelsPerUnit = 100f;
        public bool AtlasOnlyWhenNeeded = true;
        public bool FlipY = true;
        public bool PreserveViewport = true;

        public AdaptiveVectorBaker(string shapesRoot = null)
        {
            _shapesRoot = !string.IsNullOrEmpty(shapesRoot) ? shapesRoot : SourceImportPaths.ShapesRoot;
        }

        public string ShapesRoot => _shapesRoot;

        /// <summary>
        /// Computes adaptive tessellation options scaled by the shape's canvas dimensions.
        /// Scaling keeps vertex count roughly constant instead of scaling quadratically with pixel area.
        /// </summary>
        public VectorUtils.TessellationOptions GetAdaptiveOptions(float width, float height)
        {
            float maxDim = Mathf.Max(width, height);
            float refSize = Mathf.Max(1f, ReferenceSizePx);
            float scale = Mathf.Max(1f, maxDim / refSize);

            return new VectorUtils.TessellationOptions
            {
                MaxCordDeviation = BaseChordDeviation * scale,
                MaxTanAngleDeviation = BaseTanAngleDeviation,
                StepDistance = BaseStepDistance * scale,
                SamplingStepSize = BaseSamplingStepSize * scale,
            };
        }

        /// <summary>
        /// Imports and tessellates a vector shape with adaptive chord deviation.
        /// </summary>
        public List<VectorUtils.Geometry> TessellateShape(int shapeId, out VectorUtils.TextureAtlas atlas, out string error)
        {
            atlas = null;
            error = null;

            string path = ResolveShapePath(shapeId);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = $"SVG not found for shape {shapeId}";
                return null;
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                error = $"Failed reading shape {shapeId}: {ex.Message}";
                return null;
            }

            float w, h;
            if (!VectorShapeCatalog.TryReadRootSize(text, out w, out h))
            {
                w = 256f;
                h = 256f;
            }

            SVGParser.SceneInfo info;
            try
            {
                using (var reader = new StringReader(text))
                {
                    info = SVGParser.ImportSVG(
                        reader,
                        PreserveViewport ? ViewportOptions.PreserveViewport : ViewportOptions.DontPreserve,
                        1f,
                        SvgPixelsPerUnit,
                        Mathf.Max(1, Mathf.CeilToInt(w)),
                        Mathf.Max(1, Mathf.CeilToInt(h)));
                }
            }
            catch (Exception ex)
            {
                error = $"ImportSVG failed for shape {shapeId}: {ex.Message}";
                return null;
            }

            if (info.Scene == null || info.Scene.Root == null)
            {
                error = $"Empty SVG scene for shape {shapeId}";
                return null;
            }

            var options = GetAdaptiveOptions(w, h);
            List<VectorUtils.Geometry> geoms;
            try
            {
                geoms = VectorUtils.TessellateScene(info.Scene, options, info.NodeOpacity);
            }
            catch (Exception ex)
            {
                error = $"TessellateScene failed for shape {shapeId}: {ex.Message}";
                return null;
            }

            if (geoms == null || geoms.Count == 0)
            {
                error = $"Tessellation produced 0 geometry for shape {shapeId}";
                return null;
            }

            bool needsAtlas = !AtlasOnlyWhenNeeded || AnyNonSolidFill(geoms);
            if (needsAtlas)
            {
                try
                {
                    atlas = VectorUtils.GenerateAtlasAndFillUVs(geoms, AtlasRasterSize);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AdaptiveVectorBaker] Atlas generation failed for shape {shapeId}: {ex.Message}");
                }
            }

            return geoms;
        }

        /// <summary>
        /// Builds a Mesh directly from the tessellated vector shape.
        /// </summary>
        public Mesh BuildMesh(int shapeId, out VectorUtils.TextureAtlas atlas, out string error)
        {
            var geoms = TessellateShape(shapeId, out atlas, out error);
            if (geoms == null || geoms.Count == 0)
                return null;

            var mesh = new Mesh
            {
                name = $"sym{shapeId}_vector_mesh",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = IndexFormat.UInt32,
            };

            VectorUtils.FillMesh(mesh, geoms, SvgPixelsPerUnit, FlipY);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Builds a Sprite directly using VectorUtils.BuildSprite.
        /// </summary>
        public Sprite BuildSprite(int shapeId, out string error, float pixelsPerUnit = 100f)
        {
            VectorUtils.TextureAtlas atlas;
            var geoms = TessellateShape(shapeId, out atlas, out error);
            if (geoms == null || geoms.Count == 0)
                return null;

            try
            {
                var sprite = VectorUtils.BuildSprite(
                    geoms,
                    pixelsPerUnit,
                    VectorUtils.Alignment.Center,
                    Vector2.zero,
                    128,
                    FlipY);

                if (sprite != null)
                {
                    sprite.name = $"sym{shapeId}_vector_sprite";
                    sprite.hideFlags = HideFlags.HideAndDontSave;
                }
                return sprite;
            }
            catch (Exception ex)
            {
                error = $"BuildSprite failed for shape {shapeId}: {ex.Message}";
                return null;
            }
        }

        public Sprite BuildSprite(int shapeId, float pixelsPerUnit, out string error)
            => BuildSprite(shapeId, out error, pixelsPerUnit);

        static bool AnyNonSolidFill(List<VectorUtils.Geometry> geoms)
        {
            if (geoms == null) return false;
            foreach (var g in geoms)
            {
                if (g.Fill != null && !(g.Fill is SolidFill))
                    return true;
            }
            return false;
        }

        string ResolveShapePath(int shapeId)
        {
            if (string.IsNullOrEmpty(_shapesRoot)) return null;

            string standard = Path.Combine(_shapesRoot, $"{shapeId}_symbol{shapeId}.svg");
            if (File.Exists(standard)) return standard;

            string bare = Path.Combine(_shapesRoot, $"{shapeId}.svg");
            if (File.Exists(bare)) return bare;

            return null;
        }
    }
}
#endif
