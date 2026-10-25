using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map.Rendering.RoomTerrain
{
    /// <summary>
    /// The art the quarter renderer needs, in the one shape it can use: per mask, four quarter
    /// mappings; per quarter clip, five frames; per material texture, a repeating bitmap.
    ///
    /// <para><b>This asset exists because the shipped mask art cannot express a quarter.</b> The
    /// project's own <c>Art/Imported/TileMasks</c> holds one 40×40 PNG per edge <i>combination</i>
    /// (frame 3 is "top-right corner cut", frame 4 is "both top corners cut"), which is why
    /// <see cref="TileCompositor"/> has to mirror a quadrant to reach the other three and why the
    /// turns invert. A quarter renderer needs five images per quarter — the export in
    /// <c>Art/Imported/RoomTerrain/masks</c> has them (<c>clipNNNN-fN.png</c>), and the manifest says
    /// which clip belongs to which quarter and whether it is flipped. That mapping is data, so it is
    /// an asset rather than a table in code.</para>
    ///
    /// <para><b>Built by <c>RoomTerrainArtImporter</c> from the manifest, never hand-edited.</b> Every
    /// entry carries a clip id that the importer verified against a real sprite; a mask frame that is
    /// missing is an import error, not a silent hole in a wall.</para>
    /// </summary>
    [CreateAssetMenu(fileName = "RoomTerrainArt", menuName = "PFE/Map/Room Terrain Art")]
    public sealed class RoomTerrainArt : ScriptableObject
    {
        /// <summary>Five mask frames for one exported clip. <c>frames[i]</c> is edge state <c>i</c>.</summary>
        [Serializable]
        public sealed class FrameSet
        {
            public int clip;
            [Tooltip("Raster pixels per source pixel the clip was exported at (manifest `zoom`).")]
            public double rasterZoom = 8d;
            public Sprite[] frames = Array.Empty<Sprite>();
        }

        /// <summary>Which clip draws this quarter, and whether it is mirrored to get there.</summary>
        [Serializable]
        public sealed class QuarterEntry
        {
            public RoomQuarter quarter;
            public int clip;
            public bool flipX;
            public bool flipY;
        }

        /// <summary>One mask: either four quarter clips (<c>corners</c>) or one whole-cell clip.</summary>
        [Serializable]
        public sealed class MaskEntry
        {
            public string name;
            [Tooltip("`corners` or `whole`.")]
            public string kind = "corners";
            [Tooltip("Only for kind = whole.")]
            public int clip;
            public QuarterEntry[] quarters = Array.Empty<QuarterEntry>();

            public bool IsWhole => string.Equals(kind, "whole", StringComparison.OrdinalIgnoreCase);
        }

        [Serializable]
        public sealed class TextureEntry
        {
            public string name;
            public Texture2D texture;
        }

        [SerializeField, Tooltip("Source pixels per quarter (manifest `quarterPixels`).")]
        private int quarterPixels = 20;

        [SerializeField, Tooltip("Source pixels per cell (manifest `cellPixels`).")]
        private int cellPixels = 40;

        [SerializeField] private FrameSet[] frameSets = Array.Empty<FrameSet>();
        [SerializeField] private MaskEntry[] masks = Array.Empty<MaskEntry>();
        [SerializeField] private TextureEntry[] textures = Array.Empty<TextureEntry>();

        [SerializeField, Tooltip(
            "`tileFront` overlay frames. Index 0 is AS3 frame 1, i.e. `vid` 1. Ladders, stair " +
            "treads and the ledge frame on top of a stair run all live here.")]
        private Sprite[] overlayFrames = Array.Empty<Sprite>();

        [NonSerialized] private Dictionary<string, MaskEntry> _maskIndex;
        [NonSerialized] private Dictionary<int, FrameSet> _frameIndex;
        [NonSerialized] private Dictionary<string, Texture2D> _textureIndex;

        public int QuarterPixels => quarterPixels;
        public int CellPixels => cellPixels;
        public int MaskCount => masks != null ? masks.Length : 0;
        public int FrameSetCount => frameSets != null ? frameSets.Length : 0;
        public int TextureCount => textures != null ? textures.Length : 0;

        /// <summary>How many overlay frames are loaded. 16 in the shipped export, AS3 `vid` 1..16.</summary>
        public int OverlayCount => overlayFrames != null ? overlayFrames.Length : 0;

        /// <summary>
        /// Where the <c>tileFront</c> symbol's registration point sits inside its 50×110 canvas, in
        /// source pixels from the canvas's top-left corner: <b>(5, 30)</b>.
        ///
        /// <para><b>Why a constant and not a guess.</b> <c>Grafon.as:479-480</c> pins the symbol's
        /// registration point to the cell's top-left corner (<c>tile.x = i * tileX; tile.y = j *
        /// tileY</c>), so the placement is only correct if the canvas is offset by the registration.
        /// A PNG exported from a sprite gives the frame's <i>bounds</i>, not that offset, and the
        /// offset cannot be recovered from the pixels alone.</para>
        ///
        /// <para><b>Provenance.</b> Remains-Unity exported the same symbol from the same SWF and kept
        /// the number: <c>RemainsUnity/Assets/Remains/Rooms/Original/Tiles/tiles-manifest.json</c>
        /// lists <c>front:1</c> … <c>front:16</c>, all <c>width 50, height 110, zoom 1, originX 5,
        /// originY 30</c>, sourced from <c>pfeUI.swf sprite 4896</c>. Our own export corroborates it:
        /// every one of the 16 frames has its content starting at <c>y = 30</c>, and frames 5..16 (the
        /// stair treads) start at <c>x = 5</c> — i.e. the tread's top-left corner <i>is</i> the
        /// registration point.</para>
        ///
        /// <para>The consequence of ignoring it is small but visible: with a centre pivot the canvas
        /// lands 5 px right and 5 px high, which is enough to slide the art off the channel the mask
        /// carves for it.</para>
        /// </summary>
        public const int OverlayOriginXPixels = 5;

        /// <inheritdoc cref="OverlayOriginXPixels"/>
        public const int OverlayOriginYPixels = 30;

        /// <summary>
        /// The overlay sprite for an AS3 <c>vid</c> / <c>vid2</c>, or null when the id is outside the
        /// frames this asset carries.
        ///
        /// <para><b>Overlays are not cell-sized and not masked.</b> The original adds a
        /// <c>tileFront</c> child at the cell's origin (<c>Grafon.as:470-499</c>) — a 50×110 symbol on
        /// a 40 px grid, so a ladder stands 2.75 cells tall and overhangs its own cell. It is drawn
        /// into the <c>front</c> sprite <i>before</i> any material (<c>:518</c>), which is why these
        /// cannot ride on a mask Tilemap: they have their own colours, their own size, and they are
        /// under the walls rather than cut out of them.</para>
        ///
        /// <para><b><c>vid</c> is a frame number, not a material.</b> <c>Location.as:583</c> does
        /// <c>++vid</c> on the top step of a stair run, so the "ledge" is the next frame of the same
        /// ladder or tread art. A renderer that reads <c>vid</c> as a material id draws the ladder
        /// twice and the ledge never.</para>
        /// </summary>
        public Sprite Overlay(int visualId)
        {
            int index = OverlayIndex(visualId, OverlayCount);
            return index < 0 ? null : overlayFrames[index];
        }

        /// <summary>
        /// Array index for an AS3 <c>vid</c>, or -1 for "no such frame".
        ///
        /// <para><b>Separate from <see cref="Overlay"/> so it can be pinned without a sprite.</b>
        /// <c>Sprite.Create</c> is an engine call and this project's offline fixtures run against
        /// stubs, so a fixture that needs a real sprite cannot be written — and the mapping is the
        /// part that can be wrong. Both plausible mistakes are covered by the fixture: an off-by-one
        /// (<c>vid</c> 1 → index 1 skips the first ladder) and a clamp (an unknown <c>vid</c> drawing
        /// the last frame, i.e. a wrong ladder rather than no ladder).</para>
        /// </summary>
        public static int OverlayIndex(int visualId, int frameCount)
        {
            if (visualId <= 0 || frameCount <= 0) return -1;

            int index = visualId - 1;
            return index < frameCount ? index : -1;
        }

        /// <summary>
        /// The sentence for an asset with no overlay frames, or null when there are some.
        ///
        /// <para>Pure and static for the same reason as <see cref="OverlayIndex"/>:
        /// <c>ScriptableObject.CreateInstance</c> is an ECall and the offline fixtures cannot call it,
        /// so a claim that only exists inside an instance method cannot be pinned at all.</para>
        /// </summary>
        public static string ValidateOverlayCount(int frameCount)
        {
            if (frameCount > 0) return null;

            return "No overlay frames: ladders, stair treads and ledges will not be drawn. " +
                   "Rebuild via PFE/Art/Rebuild Room Terrain Art.";
        }

        /// <summary>Populated by the importer. Public so the A/B window can print what it loaded.</summary>
        public IReadOnlyList<MaskEntry> Masks => masks;

        public void Configure(
            int sourceQuarterPixels,
            int sourceCellPixels,
            FrameSet[] sets,
            MaskEntry[] maskEntries,
            TextureEntry[] textureEntries,
            Sprite[] overlayArt = null)
        {
            quarterPixels = sourceQuarterPixels;
            cellPixels = sourceCellPixels;
            frameSets = sets ?? Array.Empty<FrameSet>();
            masks = maskEntries ?? Array.Empty<MaskEntry>();
            textures = textureEntries ?? Array.Empty<TextureEntry>();
            overlayFrames = overlayArt ?? Array.Empty<Sprite>();
            Invalidate();
        }

        private void Invalidate()
        {
            _maskIndex = null;
            _frameIndex = null;
            _textureIndex = null;
        }

        private void OnEnable()
        {
            Invalidate();
        }

        public bool TryGetMask(string name, out MaskEntry mask)
        {
            mask = null;
            if (string.IsNullOrWhiteSpace(name)) return false;
            EnsureIndices();
            return _maskIndex.TryGetValue(name, out mask);
        }

        public MaskEntry GetMask(string name)
        {
            return TryGetMask(name, out MaskEntry mask) ? mask : null;
        }

        public FrameSet GetFrameSet(int clip)
        {
            EnsureIndices();
            return _frameIndex.TryGetValue(clip, out FrameSet set) ? set : null;
        }

        /// <summary>
        /// The sprite for an edge state.
        ///
        /// <para><b>State 0 is a real frame, not a skip.</b> Frame <c>N</c> in the export is state
        /// <c>N-1</c>, so state 0 is frame 1 — the plain square, which for most masks is fully opaque.
        /// The shipped compositor instead <i>skips</i> the mask at state 0 and writes the material
        /// through unmasked; the two agree wherever frame 1 is opaque and differ where the art has a
        /// bevel. Drawing the frame is the faithful choice.</para>
        /// </summary>
        public Sprite Frame(int clip, int state)
        {
            if (state < 0 || state >= RoomQuarterRules.StateCount) return null;

            FrameSet set = GetFrameSet(clip);
            if (set?.frames == null || state >= set.frames.Length) return null;

            return set.frames[state];
        }

        public Texture2D Texture(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            EnsureIndices();
            return _textureIndex.TryGetValue(name, out Texture2D texture) ? texture : null;
        }

        private void EnsureIndices()
        {
            if (_maskIndex != null && _frameIndex != null && _textureIndex != null) return;

            _maskIndex = new Dictionary<string, MaskEntry>(StringComparer.OrdinalIgnoreCase);
            if (masks != null)
            {
                foreach (MaskEntry mask in masks)
                {
                    if (mask == null || string.IsNullOrWhiteSpace(mask.name)) continue;
                    _maskIndex[mask.name] = mask;
                }
            }

            _frameIndex = new Dictionary<int, FrameSet>();
            if (frameSets != null)
            {
                foreach (FrameSet set in frameSets)
                {
                    if (set == null) continue;
                    _frameIndex[set.clip] = set;
                }
            }

            _textureIndex = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
            if (textures != null)
            {
                foreach (TextureEntry entry in textures)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.name)) continue;
                    _textureIndex[entry.name] = entry.texture;
                }
            }
        }

        /// <summary>
        /// Everything that would make this asset draw the wrong thing, as sentences.
        ///
        /// <para><b>A texture whose wrap mode is not Repeat is the quiet one.</b> The shader samples the
        /// material bitmap in world space across the whole room, so a Clamp texture shows its own edge
        /// stretched across every cell after the first — a defect that reads as "the wall material is
        /// smeared" and gets blamed on the projection maths. Checking it at build time is cheaper than
        /// diagnosing it from a screenshot.</para>
        /// </summary>
        public List<string> Validate()
        {
            var issues = new List<string>();

            if (quarterPixels <= 0) issues.Add($"quarterPixels is {quarterPixels}; it must be positive.");
            if (cellPixels <= 0) issues.Add($"cellPixels is {cellPixels}; it must be positive.");

            if (frameSets == null || frameSets.Length == 0)
            {
                issues.Add("No frame sets: no mask can be drawn.");
            }
            else
            {
                foreach (FrameSet set in frameSets)
                {
                    if (set == null) { issues.Add("A frame set is null."); continue; }

                    if (set.frames != null)
                    {
                        for (int i = 0; i < set.frames.Length; i++)
                        {
                            if (set.frames[i] == null)
                                issues.Add($"clip {set.clip} frame {i + 1} (state {i}) has no sprite.");
                        }
                    }

                    if (set.rasterZoom <= 0d)
                        issues.Add($"clip {set.clip} has rasterZoom {set.rasterZoom}; scale would be degenerate.");
                }
            }

            if (masks == null || masks.Length == 0)
            {
                issues.Add("No masks: every cell would fall back to the plain square.");
            }
            else
            {
                foreach (MaskEntry mask in masks)
                {
                    if (mask == null) { issues.Add("A mask entry is null."); continue; }

                    if (!mask.IsWhole && (mask.quarters == null || mask.quarters.Length == 0))
                    {
                        issues.Add($"mask '{mask.name}' has no quarters and is not `whole`.");
                        continue;
                    }

                    if (mask.IsWhole && GetFrameSet(mask.clip) == null)
                        issues.Add($"mask '{mask.name}' (whole) references clip {mask.clip}, which has no frames.");

                    if (mask.quarters == null) continue;

                    foreach (QuarterEntry quarter in mask.quarters)
                    {
                        if (quarter == null) { issues.Add($"mask '{mask.name}' has a null quarter entry."); continue; }

                        FrameSet set = GetFrameSet(quarter.clip);
                        if (set == null)
                        {
                            issues.Add($"mask '{mask.name}' quarter {RoomQuarterRules.Name(quarter.quarter)} references clip {quarter.clip}, which has no frames.");
                            continue;
                        }

                        // Only a corner clip needs the full state set: the whole-cell square is a
                        // single frame by design, and demanding five of it would be a false alarm.
                        int count = set.frames != null ? set.frames.Length : 0;
                        if (count < RoomQuarterRules.StateCount)
                        {
                            issues.Add($"mask '{mask.name}' quarter {RoomQuarterRules.Name(quarter.quarter)} uses clip {quarter.clip} with {count} frame(s); a corner mask needs {RoomQuarterRules.StateCount}.");
                        }
                    }
                }
            }

            if (textures == null || textures.Length == 0)
            {
                issues.Add("No material textures: nothing would be sampled inside the mask.");
            }
            else
            {
                foreach (TextureEntry entry in textures)
                {
                    if (entry == null) continue;
                    if (entry.texture == null)
                    {
                        issues.Add($"texture '{entry.name}' is null.");
                        continue;
                    }

                    if (entry.texture.wrapMode != TextureWrapMode.Repeat)
                        issues.Add($"texture '{entry.name}' has wrapMode {entry.texture.wrapMode}; the world-space projection needs Repeat.");
                }
            }

            // Overlays are the one part that is *not* an error when absent: the renderer draws the
            // room without them and says so. Reporting the count here is what turns "ladders are
            // missing" into "the art asset was built before overlays existed, rebuild it".
            string overlayIssue = ValidateOverlayCount(OverlayCount);
            if (overlayIssue != null) issues.Add(overlayIssue);

            if (overlayFrames != null)
            {
                for (int i = 0; i < overlayFrames.Length; i++)
                {
                    if (overlayFrames[i] == null)
                        issues.Add($"overlay frame {i + 1} (AS3 vid {i + 1}) has no sprite.");
                }
            }

            return issues;
        }
    }
}
