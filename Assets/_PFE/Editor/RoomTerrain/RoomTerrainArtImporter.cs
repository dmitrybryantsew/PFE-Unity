#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using PFE.Systems.Map.Rendering.RoomTerrain;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.RoomTerrain
{
    /// <summary>
    /// Builds <see cref="RoomTerrainArt"/> from the exported tile manifest.
    ///
    /// <para><b>Why this importer exists at all, and why it is this small.</b> The renderer needs four
    /// things it cannot derive: which clip draws which quarter, whether that clip is mirrored, how many
    /// raster pixels the export used, and where the mask PNGs live. All four are in
    /// <c>tiles-manifest.json</c>, which is a declaration made by the exporter — so this is a
    /// transcription job, not a heuristic one. It reads the manifest, loads what the manifest names,
    /// and fails loudly on anything it cannot load. There is no shape guessing here, which is the whole
    /// reason the mask art was re-exported rather than reverse-engineered out of the 40×40 combination
    /// frames the project already had.</para>
    ///
    /// <para><b>Material textures are not copied.</b> The manifest names 87 bitmaps and they split
    /// cleanly by <i>where the project already keeps them</i>, which is also what decides their role:
    /// 78 are tile fills in <c>Art/TileTextures</c> (the same source art the shipped compositor
    /// samples, already imported with Repeat wrapping), and the remaining 9 are the <c>fon*</c>
    /// land-wide painted backdrops in <c>Art/Imported/RoomBackgrounds/&lt;name&gt;/1.png</c>, imported
    /// by <c>RoomGraphicsImporter</c> for <c>LandDefinition.backdropId</c>. Backdrops are <b>not</b>
    /// carried in this asset: it is a texture lookup for tile materials, and a 1920×1000 Clamp-wrapped
    /// backdrop in that table would be a lie about what the table means. They are reported once and
    /// skipped. A name that resolves in neither place is an error.</para>
    /// </summary>
    public static class RoomTerrainArtImporter
    {
        public const string ArtRoot = "Assets/_PFE/Art/Imported/RoomTerrain";
        public const string ManifestPath = ArtRoot + "/tiles-manifest.json";
        public const string MaskRoot = ArtRoot + "/masks";
        public const string TextureRoot = "Assets/_PFE/Art/TileTextures";

        /// <summary>Where <c>RoomGraphicsImporter</c> puts the <c>fon*</c> backdrops. Not terrain.</summary>
        public const string BackdropRoot = "Assets/_PFE/Art/Imported/RoomBackgrounds";

        /// <summary>
        /// Where <c>RoomGraphicsImporter</c> puts the <c>tileFront</c> symbol frames — the ladders,
        /// stair treads and ledges. <c>N.png</c> is AS3 <c>vid</c> <c>N</c>.
        ///
        /// <para><b>Read from the shipped import, not from the manifest.</b> The manifest describes
        /// material art; these frames come from a SWF library symbol, and the project already imports
        /// them for <c>TileAssetDatabase</c> as <c>tile_N</c>. Re-exporting them here would be a second
        /// copy of the same PNGs, and a second copy is what drifts.</para>
        /// </summary>
        public const string OverlayRoot = "Assets/_PFE/Art/Imported/TileOverlays/tileFront";

        public const string AssetPath = "Assets/_PFE/Data/RoomTerrainArt.asset";

        private const string ManifestFormat = "remains-tile-manifest";

        [MenuItem("PFE/Art/Rebuild Room Terrain Art", priority = 210)]
        public static void Build()
        {
            var manifestAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestPath);
            if (manifestAsset == null)
            {
                Debug.LogError($"[RoomTerrainArt] No manifest at {ManifestPath}. Nothing was built.");
                return;
            }

            ManifestDto manifest;
            try
            {
                manifest = JsonUtility.FromJson<ManifestDto>(manifestAsset.text);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[RoomTerrainArt] Manifest is not readable JSON: {exception.Message}");
                return;
            }

            if (manifest == null || !string.Equals(manifest.format, ManifestFormat, StringComparison.Ordinal))
            {
                Debug.LogError(
                    $"[RoomTerrainArt] {ManifestPath} is not a `{ManifestFormat}` manifest " +
                    $"(read format '{manifest?.format}'). Nothing was built.");
                return;
            }

            if (manifest.masks == null || manifest.masks.Length == 0)
            {
                Debug.LogError($"[RoomTerrainArt] {ManifestPath} declares no masks. Nothing was built.");
                return;
            }

            var errors = new List<string>();
            var notes = new List<string>();

            Dictionary<int, double> clipZooms = CollectClipZooms(manifest);
            RoomTerrainArt.FrameSet[] frameSets = LoadFrameSets(clipZooms, errors);
            RoomTerrainArt.MaskEntry[] masks = BuildMasks(manifest, errors);
            RoomTerrainArt.TextureEntry[] textures = LoadTextures(manifest, errors, notes);
            Sprite[] overlays = LoadOverlays(errors, notes);

            if (errors.Count > 0)
            {
                foreach (string error in errors)
                {
                    Debug.LogError($"[RoomTerrainArt] {error}");
                }

                Debug.LogError(
                    $"[RoomTerrainArt] {errors.Count} problem(s); the asset was NOT written. A mask " +
                    "with a missing frame draws a hole in a wall, and a material texture that is " +
                    "absent leaves the tile untextured — both read as geometry bugs, so neither is " +
                    "worth building around.");
                return;
            }

            foreach (string note in notes)
            {
                Debug.Log($"[RoomTerrainArt] {note}");
            }

            RoomTerrainArt asset = AssetDatabase.LoadAssetAtPath<RoomTerrainArt>(AssetPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<RoomTerrainArt>();
                AssetDatabase.CreateAsset(asset, AssetPath);
            }

            asset.Configure(manifest.quarterPixels, manifest.cellPixels, frameSets, masks, textures, overlays);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            List<string> issues = asset.Validate();
            foreach (string issue in issues)
            {
                Debug.LogWarning($"[RoomTerrainArt] validate: {issue}");
            }

            Debug.Log(
                $"[RoomTerrainArt] Built {AssetPath}: {frameSets.Length} clip(s), {masks.Length} mask(s), " +
                $"{textures.Length} texture(s), {overlays.Length} overlay frame(s), " +
                $"cell {manifest.cellPixels}px / quarter {manifest.quarterPixels}px. " +
                (issues.Count == 0 ? "Validation clean." : $"{issues.Count} validation warning(s)."));
        }

        /// <summary>
        /// Re-runs validation on the existing asset and prints every issue. Separate from
        /// <see cref="Build"/> so a broken asset can be diagnosed without rewriting it.
        /// </summary>
        [MenuItem("PFE/Art/Validate Room Terrain Art", priority = 211)]
        public static void ValidateExisting()
        {
            var asset = AssetDatabase.LoadAssetAtPath<RoomTerrainArt>(AssetPath);
            if (asset == null)
            {
                Debug.LogError($"[RoomTerrainArt] No asset at {AssetPath}.");
                return;
            }

            List<string> issues = asset.Validate();
            if (issues.Count == 0)
            {
                Debug.Log(
                    $"[RoomTerrainArt] {AssetPath} is clean: {asset.FrameSetCount} clip(s), " +
                    $"{asset.MaskCount} mask(s), {asset.TextureCount} texture(s).");
                return;
            }

            foreach (string issue in issues)
            {
                Debug.LogError($"[RoomTerrainArt] {issue}");
            }

            Debug.LogError($"[RoomTerrainArt] {issues.Count} issue(s).");
        }

        // ─── Manifest → asset ────────────────────────────────────

        /// <summary>
        /// The manifest's `zoom` is per art entry; every frame of a clip shares one. Collected first so
        /// a clip whose frames disagree is caught here rather than producing a subtly scaled quarter.
        /// </summary>
        private static Dictionary<int, double> CollectClipZooms(ManifestDto manifest)
        {
            var zooms = new Dictionary<int, double>();
            if (manifest.art == null) return zooms;

            foreach (ArtDto art in manifest.art)
            {
                if (art == null || string.IsNullOrEmpty(art.id)) continue;
                if (!art.id.StartsWith("mask:", StringComparison.Ordinal)) continue;

                string[] parts = art.id.Split(':');
                if (parts.Length != 3 || !int.TryParse(parts[1], out int clip)) continue;

                if (zooms.TryGetValue(clip, out double existing))
                {
                    if (Math.Abs(existing - art.zoom) > 0.0001d)
                    {
                        Debug.LogWarning(
                            $"[RoomTerrainArt] clip {clip} declares two zooms ({existing} and {art.zoom}); " +
                            "using the first. A clip cannot be rasterised at two scales.");
                    }

                    continue;
                }

                zooms[clip] = art.zoom;
            }

            return zooms;
        }

        private static RoomTerrainArt.FrameSet[] LoadFrameSets(Dictionary<int, double> clipZooms, List<string> errors)
        {
            var sets = new List<RoomTerrainArt.FrameSet>(clipZooms.Count);

            foreach (KeyValuePair<int, double> pair in clipZooms)
            {
                int clip = pair.Key;
                var frames = new List<Sprite>(RoomQuarterRules.StateCount);

                for (int frame = 1; frame <= RoomQuarterRules.StateCount; frame++)
                {
                    string path = $"{MaskRoot}/clip{clip}-f{frame}.png";
                    var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                    if (sprite == null)
                    {
                        // A whole-cell mask legitimately has one frame; a corner clip does not.
                        // Which of the two this is is decided by the masks table, so stop at the first
                        // gap and let the caller judge — a gap in the middle is always an error.
                        break;
                    }

                    frames.Add(sprite);
                }

                if (frames.Count == 0)
                {
                    errors.Add($"clip {clip}: no frames found under {MaskRoot}.");
                    continue;
                }

                sets.Add(new RoomTerrainArt.FrameSet
                {
                    clip = clip,
                    rasterZoom = pair.Value,
                    frames = frames.ToArray()
                });
            }

            return sets.ToArray();
        }

        private static RoomTerrainArt.MaskEntry[] BuildMasks(ManifestDto manifest, List<string> errors)
        {
            var masks = new List<RoomTerrainArt.MaskEntry>(manifest.masks.Length);

            foreach (MaskDto source in manifest.masks)
            {
                if (source == null || string.IsNullOrWhiteSpace(source.name))
                {
                    errors.Add("A mask entry has no name.");
                    continue;
                }

                bool whole = string.Equals(source.kind, "whole", StringComparison.OrdinalIgnoreCase);
                var quarters = new List<RoomTerrainArt.QuarterEntry>();

                if (!whole)
                {
                    if (source.quarters == null || source.quarters.Length == 0)
                    {
                        errors.Add($"mask '{source.name}' is kind '{source.kind}' but declares no quarters.");
                        continue;
                    }

                    foreach (QuarterDto quarter in source.quarters)
                    {
                        if (quarter == null)
                        {
                            errors.Add($"mask '{source.name}' has a null quarter.");
                            continue;
                        }

                        if (!RoomQuarterRules.TryParse(quarter.quarter, out RoomQuarter parsed))
                        {
                            errors.Add($"mask '{source.name}' names quarter '{quarter.quarter}', which is not a quarter.");
                            continue;
                        }

                        quarters.Add(new RoomTerrainArt.QuarterEntry
                        {
                            quarter = parsed,
                            clip = quarter.clip,
                            flipX = quarter.flipX,
                            flipY = quarter.flipY
                        });
                    }

                    if (quarters.Count != 4)
                    {
                        // maskFloor is the one legitimate three-quarter mask in this export: it draws a
                        // floor band on the upper half only, so its bottom quarters genuinely do not
                        // exist. Anything else missing a quarter is a transcription error.
                        if (!string.Equals(source.name, "maskFloor", StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.LogWarning(
                                $"[RoomTerrainArt] mask '{source.name}' declares {quarters.Count} quarter(s); " +
                                "the two bottom ones will simply not be drawn.");
                        }
                    }
                }
                else if (source.clip <= 0)
                {
                    errors.Add($"mask '{source.name}' is whole but declares clip {source.clip}.");
                    continue;
                }

                masks.Add(new RoomTerrainArt.MaskEntry
                {
                    name = source.name,
                    kind = whole ? "whole" : "corners",
                    clip = source.clip,
                    quarters = quarters.ToArray()
                });
            }

            return masks.ToArray();
        }

        /// <summary>
        /// Loads the <c>tileFront</c> frames that carry every ladder, stair tread and ledge.
        ///
        /// <para><b>Order is the data.</b> <c>N.png</c> is AS3 <c>vid</c> <c>N</c>, and the frame is
        /// selected by a bare integer from the room string, so the array's index <i>is</i> the id. A
        /// missing frame in the middle would silently shift every frame after it — a ladder drawing as
        /// a ledge, which is exactly the defect this pass exists to fix. So a gap is an error rather
        /// than a shorter array, and the loader walks the numbers instead of sorting filenames
        /// (a string sort puts 10 before 2).</para>
        ///
        /// <para>An absent folder is also an error: a room built without overlays is a room whose
        /// ladders are missing, and that is not a state worth writing an asset into.</para>
        /// </summary>
        private static Sprite[] LoadOverlays(List<string> errors, List<string> notes)
        {
            if (!AssetDatabase.IsValidFolder(OverlayRoot))
            {
                errors.Add(
                    $"no overlay frames at {OverlayRoot}. Ladders, stair treads and ledges would not " +
                    "be drawn. Run the room graphics import first (it writes tileFront there).");
                return Array.Empty<Sprite>();
            }

            var frames = new List<Sprite>();
            int frame = 1;
            while (true)
            {
                string path = OverlayRoot + "/" + frame + ".png";
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null) break;

                frames.Add(sprite);
                frame++;
            }

            if (frames.Count == 0)
            {
                errors.Add(
                    $"{OverlayRoot} exists but holds no loadable sprite at 1.png. Check the texture " +
                    "importer's type (it must be Sprite) before rebuilding.");
                return Array.Empty<Sprite>();
            }

            // A gap after the last contiguous frame is the dangerous case: the loop stopped early.
            string[] onDisk = Directory.GetFiles(OverlayRoot, "*.png");
            if (onDisk.Length != frames.Count)
            {
                errors.Add(
                    $"{OverlayRoot} holds {onDisk.Length} PNG(s) but only {frames.Count} are contiguous " +
                    "from 1. A gap would shift every later frame, so the frames are treated as unloadable.");
                return Array.Empty<Sprite>();
            }

            notes.Add($"overlays: {frames.Count} tileFront frame(s), vid 1..{frames.Count}.");
            return frames.ToArray();
        }

        /// <summary>
        /// Resolves every `texture:` entry by name against this project's own tile textures, and
        /// classifies the rest.
        ///
        /// <para>The manifest also carries its own copies under <c>textures/</c>, and they are not used:
        /// the names it lists are already in the project, and pointing at the local copies keeps one
        /// copy of the art and keeps the A/B sampling the same pixels the shipped compositor samples.
        /// 78 resolve from <see cref="TextureRoot"/> — that index is built with
        /// <c>FindAssets("t:Texture2D")</c>, so it is <i>extension-agnostic</i>; most tile fills are
        /// <c>.jpg</c>, and matching only <c>.png</c> undercounts them 19-vs-78.</para>
        ///
        /// <para>The other 9 are <c>fon*</c> land backdrops under <see cref="BackdropRoot"/>. They are
        /// counted in one note and skipped rather than added: this asset maps a material name to a
        /// tile fill, and a backdrop is neither Repeat-wrapped nor tile-sized. Skipping them is a
        /// decision, so it is printed — a silent drop would resurface as "why is the backdrop layer
        /// missing" the day someone builds one.</para>
        /// </summary>
        private static RoomTerrainArt.TextureEntry[] LoadTextures(
            ManifestDto manifest,
            List<string> errors,
            List<string> notes)
        {
            Dictionary<string, Texture2D> available = IndexTileTextures();
            HashSet<string> backdropNames = IndexBackdropNames();
            var textures = new List<RoomTerrainArt.TextureEntry>();

            if (manifest.art == null) return textures.ToArray();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var backdrops = new List<string>();

            foreach (ArtDto art in manifest.art)
            {
                if (art == null || string.IsNullOrEmpty(art.id)) continue;
                if (!art.id.StartsWith("texture:", StringComparison.Ordinal)) continue;

                string name = art.id.Substring("texture:".Length);
                if (!seen.Add(name)) continue;

                if (available.TryGetValue(name, out Texture2D texture))
                {
                    // _TexelsPerMeter is derived as one texel per source pixel (see
                    // RoomTerrainRenderer.MaterialFor). A fill exported at another zoom is not wrong
                    // art, it is art this projection cannot address, and it would sample N× too dense
                    // with no visible error — the material would just look "too small".
                    if (Math.Abs(art.zoom - 1d) > 0.0001d)
                    {
                        errors.Add(
                            $"texture '{name}' is exported at zoom {art.zoom}, but the world-space " +
                            "projection assumes one texel per source pixel. Re-export at zoom 1, or " +
                            "make the renderer divide _TexelsPerMeter by the texture's zoom.");
                        continue;
                    }

                    textures.Add(new RoomTerrainArt.TextureEntry { name = name, texture = texture });
                    continue;
                }

                if (backdropNames.Contains(name))
                {
                    backdrops.Add(name);
                    continue;
                }

                errors.Add(
                    $"texture '{name}' resolves to neither {TextureRoot}/{name}.* nor " +
                    $"{BackdropRoot}/{name}/1.png.");
            }

            if (backdrops.Count > 0)
            {
                notes.Add(
                    $"{backdrops.Count} texture(s) skipped as land backdrops (not tile fills): " +
                    string.Join(", ", backdrops) + $". They live in {BackdropRoot}.");
            }

            return textures.ToArray();
        }

        /// <summary>
        /// Folder names under <see cref="BackdropRoot"/>, one <c>FindAssets</c> call rather than one per
        /// unresolved texture — a search over a folder that does not exist is not a cheap way to ask
        /// "does this exist".
        /// </summary>
        private static HashSet<string> IndexBackdropNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { BackdropRoot });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string folder = Path.GetFileName(Path.GetDirectoryName(path));
                if (!string.IsNullOrEmpty(folder)) names.Add(folder);
            }

            return names;
        }

        private static Dictionary<string, Texture2D> IndexTileTextures()
        {
            var index = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { TextureRoot });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture == null) continue;

                string name = Path.GetFileNameWithoutExtension(path);
                if (!index.ContainsKey(name)) index[name] = texture;
            }

            return index;
        }

        // ─── Manifest shape (JsonUtility) ────────────────────────

        [Serializable]
        private sealed class ManifestDto
        {
            public string format;
            public int version;
            public int quarterPixels;
            public int cellPixels;
            public MaskDto[] masks;
            public ArtDto[] art;
        }

        [Serializable]
        private sealed class MaskDto
        {
            public string name;
            public string kind;
            public int symbol;
            public int clip;
            public QuarterDto[] quarters;
        }

        [Serializable]
        private sealed class QuarterDto
        {
            public string quarter;
            public int clip;
            public bool flipX;
            public bool flipY;
        }

        [Serializable]
        private sealed class ArtDto
        {
            public string id;
            public string kind;
            public string file;
            public int width;
            public int height;
            public double zoom;
            public double originX;
            public double originY;
        }
    }
}
#endif
