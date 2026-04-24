#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Editor window that parses the SWF and dumps the full child tree of
    /// visMainMenu (symbol 559) — showing every sub-symbol, instance name,
    /// position, type (shape vs sprite), and frame count.
    /// Used to identify which pieces compose the main menu background
    /// before building the import pipeline.
    /// </summary>
    public class MainMenuSWFExplorer : EditorWindow
    {
        static readonly string DefaultSwfPath = SourceImportPaths.AssetsSwfPath;

        static readonly string DefaultJpexsRoot = SourceImportPaths.AssetsExportRoot;

        const int VisMainMenuSymbolId = 559;

        string _swfPath = DefaultSwfPath;
        string _jpexsRoot = DefaultJpexsRoot;
        Vector2 _scrollPos;
        SWFFile _parsedSwf;
        List<TreeNode> _tree;
        string _rawDump;
        bool _showRawDump;
        bool _showImages = true;
        int _maxDepth = 4;
        int _exploreSymbolId = VisMainMenuSymbolId;

        // Image preview cache
        readonly Dictionary<int, Texture2D> _imageCache = new();

        [MenuItem("PFE/Art/Main Menu SWF Explorer")]
        public static void ShowWindow()
        {
            var window = GetWindow<MainMenuSWFExplorer>("Main Menu SWF Explorer");
            window.minSize = new Vector2(700, 500);
        }

        void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Main Menu SWF Explorer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Parses assets.swf and explores the display tree of visMainMenu (symbol 559).\n" +
                "Shows all child symbols, instance names, positions, and identifies shape vs sprite pieces.\n" +
                "Use this to find the exact symbol IDs needed for the main menu import pipeline.",
                MessageType.Info);
            EditorGUILayout.Space(4);

            // Paths
            EditorGUILayout.BeginHorizontal();
            _swfPath = EditorGUILayout.TextField("SWF File", _swfPath);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string path = EditorUtility.OpenFilePanel("Select assets.swf", Path.GetDirectoryName(_swfPath), "swf");
                if (!string.IsNullOrEmpty(path)) _swfPath = path;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _jpexsRoot = EditorGUILayout.TextField("JPEXS Export", _jpexsRoot);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string path = EditorUtility.OpenFolderPanel("Select JPEXS export folder", _jpexsRoot, "");
                if (!string.IsNullOrEmpty(path)) _jpexsRoot = path;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            _exploreSymbolId = EditorGUILayout.IntField("Root Symbol ID", _exploreSymbolId);
            _maxDepth = EditorGUILayout.IntSlider("Max Depth", _maxDepth, 1, 10);
            _showImages = EditorGUILayout.Toggle("Show Image Previews", _showImages);

            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Parse SWF & Explore", GUILayout.Height(30)))
                ParseAndExplore();
            if (_tree != null && GUILayout.Button("Export to Text File", GUILayout.Height(30)))
                ExportToFile();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);

            if (_parsedSwf != null)
            {
                EditorGUILayout.LabelField(
                    $"SWF: {_parsedSwf.Symbols.Count} sprites, {_parsedSwf.ShapeBounds.Count} shapes, " +
                    $"Stage: {_parsedSwf.StageRect.width}x{_parsedSwf.StageRect.height}, " +
                    $"FPS: {_parsedSwf.FrameRate}",
                    EditorStyles.miniLabel);
            }

            // Toggle raw dump
            if (_rawDump != null)
            {
                _showRawDump = EditorGUILayout.Foldout(_showRawDump, "Raw Text Dump");
                if (_showRawDump)
                {
                    EditorGUILayout.TextArea(_rawDump, GUILayout.MaxHeight(300));
                }
            }

            // Tree view
            if (_tree != null && _tree.Count > 0)
            {
                EditorGUILayout.LabelField("Display Tree", EditorStyles.boldLabel);
                _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
                foreach (var node in _tree)
                    DrawNode(node, 0);
                EditorGUILayout.EndScrollView();
            }
        }

        void ParseAndExplore()
        {
            if (!File.Exists(_swfPath))
            {
                EditorUtility.DisplayDialog("Error", $"SWF file not found:\n{_swfPath}", "OK");
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar("Parsing SWF", "Reading binary...", 0.2f);
                var parser = new SWFParser();
                _parsedSwf = parser.Parse(_swfPath);

                EditorUtility.DisplayProgressBar("Parsing SWF", "Building tree...", 0.7f);

                if (!_parsedSwf.Symbols.TryGetValue(_exploreSymbolId, out var rootSymbol))
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error",
                        $"Symbol {_exploreSymbolId} not found in SWF.\n" +
                        $"Available symbol count: {_parsedSwf.Symbols.Count}", "OK");
                    return;
                }

                _tree = BuildTree(rootSymbol, _exploreSymbolId, 0);

                // Build raw text dump
                var sb = new StringBuilder();
                sb.AppendLine($"=== Symbol {_exploreSymbolId} — {rootSymbol.FrameCount} frames, {rootSymbol.Frames.Count} parsed frames ===");
                sb.AppendLine();
                DumpTree(sb, _tree, 0);
                _rawDump = sb.ToString();

                _imageCache.Clear();

                Debug.Log($"[MainMenuSWFExplorer] Explored symbol {_exploreSymbolId}: " +
                          $"{CountNodes(_tree)} nodes in tree");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MainMenuSWFExplorer] Parse failed: {ex}");
                EditorUtility.DisplayDialog("Error", $"Parse failed:\n{ex.Message}", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Repaint();
        }

        List<TreeNode> BuildTree(SWFSymbol symbol, int symbolId, int depth)
        {
            var nodes = new List<TreeNode>();

            if (symbol.Frames.Count == 0)
                return nodes;

            // Use frame 1 as the canonical display list
            var frame1 = symbol.Frames[0];

            foreach (var placement in frame1.Placements)
            {
                var node = new TreeNode
                {
                    SymbolId = placement.CharacterId,
                    InstanceName = placement.InstanceName,
                    Depth = placement.Depth,
                    Position = placement.Position,
                    Rotation = placement.Rotation,
                    Scale = placement.Scale,
                    HasColorTransform = placement.HasColorTransform,
                    ColorMultiply = placement.ColorMultiply,
                    ColorAdd = placement.ColorAdd,
                };

                // Determine type
                if (_parsedSwf.ShapeBounds.TryGetValue(placement.CharacterId, out var shapeBounds))
                {
                    node.Type = NodeType.Shape;
                    node.Bounds = shapeBounds;
                    node.HasExportedImage = CheckImageExists(placement.CharacterId);
                }
                else if (_parsedSwf.Symbols.TryGetValue(placement.CharacterId, out var childSymbol))
                {
                    node.Type = NodeType.Sprite;
                    node.FrameCount = childSymbol.FrameCount;
                    node.ParsedFrameCount = childSymbol.Frames.Count;
                    node.Bounds = childSymbol.Bounds;
                    node.HasExportedSprite = CheckSpriteExists(placement.CharacterId);

                    // Check for frame labels
                    node.FrameLabels = childSymbol.Frames
                        .Where(f => f.Label != null)
                        .Select(f => $"{f.FrameNumber}:{f.Label}")
                        .ToList();

                    // Recurse
                    if (depth < _maxDepth)
                        node.Children = BuildTree(childSymbol, placement.CharacterId, depth + 1);
                }
                else
                {
                    node.Type = NodeType.Unknown;
                }

                nodes.Add(node);
            }

            // Also note if there are multiple frames with different placements
            if (symbol.Frames.Count > 1)
            {
                var frame1Ids = new HashSet<int>(frame1.Placements.Select(p => p.CharacterId));
                foreach (var frame in symbol.Frames.Skip(1))
                {
                    foreach (var p in frame.Placements)
                    {
                        if (!frame1Ids.Contains(p.CharacterId))
                        {
                            nodes.Add(new TreeNode
                            {
                                SymbolId = p.CharacterId,
                                InstanceName = p.InstanceName,
                                Depth = p.Depth,
                                Position = p.Position,
                                Scale = p.Scale,
                                Rotation = p.Rotation,
                                Type = _parsedSwf.ShapeBounds.ContainsKey(p.CharacterId)
                                    ? NodeType.Shape : NodeType.Sprite,
                                Note = $"(appears on frame {frame.FrameNumber}, not frame 1)",
                            });
                            frame1Ids.Add(p.CharacterId);
                        }
                    }
                }
            }

            return nodes;
        }

        bool CheckImageExists(int symbolId)
        {
            string path = Path.Combine(_jpexsRoot, "images", $"{symbolId}_symbol{symbolId}.png");
            if (File.Exists(path)) return true;
            // Also check the root pfe images folder
            string altPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(_jpexsRoot)),
                "images", $"{symbolId}.png");
            return File.Exists(altPath);
        }

        bool CheckSpriteExists(int symbolId)
        {
            // Check for JPEXS DefineSprite export folder
            string spritesRoot = Path.Combine(_jpexsRoot, "sprites");
            if (Directory.Exists(spritesRoot))
            {
                var dirs = Directory.GetDirectories(spritesRoot, $"DefineSprite_{symbolId}_*");
                if (dirs.Length > 0) return true;
            }
            // Also check parent sprites folder
            string parentSprites = Path.Combine(
                Path.GetDirectoryName(Path.GetDirectoryName(_jpexsRoot)), "sprites");
            if (Directory.Exists(parentSprites))
            {
                var dirs = Directory.GetDirectories(parentSprites, $"DefineSprite_{symbolId}_*");
                if (dirs.Length > 0) return true;
            }
            return false;
        }

        string FindImagePath(int symbolId)
        {
            string path = Path.Combine(_jpexsRoot, "images", $"{symbolId}_symbol{symbolId}.png");
            if (File.Exists(path)) return path;
            string altPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(_jpexsRoot)),
                "images", $"{symbolId}.png");
            if (File.Exists(altPath)) return altPath;
            return null;
        }

        string FindSpritePngPath(int symbolId)
        {
            // Check parent sprites folder (where JPEXS exports named sprites)
            string parentSprites = Path.Combine(
                Path.GetDirectoryName(Path.GetDirectoryName(_jpexsRoot)), "sprites");
            if (Directory.Exists(parentSprites))
            {
                var dirs = Directory.GetDirectories(parentSprites, $"DefineSprite_{symbolId}_*");
                if (dirs.Length > 0)
                {
                    string png = Path.Combine(dirs[0], "1.png");
                    if (File.Exists(png)) return png;
                }
            }
            return null;
        }

        Texture2D LoadPreviewImage(int symbolId)
        {
            if (_imageCache.TryGetValue(symbolId, out var cached))
                return cached;

            string path = FindImagePath(symbolId) ?? FindSpritePngPath(symbolId);
            if (path == null)
            {
                _imageCache[symbolId] = null;
                return null;
            }

            try
            {
                byte[] data = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2);
                if (tex.LoadImage(data))
                {
                    _imageCache[symbolId] = tex;
                    return tex;
                }
            }
            catch
            {
                // Ignore load errors
            }

            _imageCache[symbolId] = null;
            return null;
        }

        void DrawNode(TreeNode node, int indent)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(indent * 20);

            // Type icon
            string typeIcon = node.Type switch
            {
                NodeType.Shape => "[Shape]",
                NodeType.Sprite => "[Sprite]",
                _ => "[???]"
            };

            // Color based on type
            var style = new GUIStyle(EditorStyles.label);
            if (node.Type == NodeType.Shape)
                style.normal.textColor = new Color(0.4f, 0.8f, 0.4f);
            else if (node.Type == NodeType.Sprite)
                style.normal.textColor = new Color(0.5f, 0.7f, 1.0f);

            // Name
            string name = !string.IsNullOrEmpty(node.InstanceName)
                ? $"\"{node.InstanceName}\""
                : "(unnamed)";

            // Info line
            string info = $"{typeIcon} {name}  id={node.SymbolId}  depth={node.Depth}  " +
                          $"pos=({node.Position.x:F1}, {node.Position.y:F1})  " +
                          $"scale=({node.Scale.x:F2}, {node.Scale.y:F2})";

            if (node.Rotation != 0)
                info += $"  rot={node.Rotation:F1}°";

            if (node.Type == NodeType.Shape && node.Bounds.width > 0)
                info += $"  bounds={node.Bounds.width:F0}x{node.Bounds.height:F0}";

            if (node.Type == NodeType.Sprite)
                info += $"  frames={node.FrameCount}({node.ParsedFrameCount} parsed)";

            if (node.HasExportedImage)
                info += "  [IMG]";
            if (node.HasExportedSprite)
                info += "  [SPR]";

            if (node.HasColorTransform)
                info += $"  color=({node.ColorMultiply.r:F2},{node.ColorMultiply.g:F2},{node.ColorMultiply.b:F2},{node.ColorMultiply.a:F2})";

            if (!string.IsNullOrEmpty(node.Note))
                info += $"  {node.Note}";

            EditorGUILayout.LabelField(info, style);
            EditorGUILayout.EndHorizontal();

            // Frame labels
            if (node.FrameLabels != null && node.FrameLabels.Count > 0)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space((indent + 1) * 20);
                EditorGUILayout.LabelField(
                    $"Labels: {string.Join(", ", node.FrameLabels)}",
                    EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }

            // Image preview
            if (_showImages && (node.HasExportedImage || node.HasExportedSprite))
            {
                var tex = LoadPreviewImage(node.SymbolId);
                if (tex != null)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space((indent + 1) * 20);
                    float maxH = 64;
                    float aspect = (float)tex.width / tex.height;
                    float w = Mathf.Min(maxH * aspect, 200);
                    GUILayout.Label(tex, GUILayout.Width(w), GUILayout.Height(maxH));
                    EditorGUILayout.LabelField($"{tex.width}x{tex.height}", EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();
                }
            }

            // Children
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                    DrawNode(child, indent + 1);
            }
        }

        void DumpTree(StringBuilder sb, List<TreeNode> nodes, int indent)
        {
            string pad = new string(' ', indent * 2);
            foreach (var node in nodes)
            {
                string name = !string.IsNullOrEmpty(node.InstanceName) ? $"\"{node.InstanceName}\"" : "(unnamed)";
                string type = node.Type.ToString();

                sb.Append($"{pad}[{type}] {name} id={node.SymbolId} depth={node.Depth}");
                sb.Append($" pos=({node.Position.x:F1},{node.Position.y:F1})");
                sb.Append($" scale=({node.Scale.x:F2},{node.Scale.y:F2})");

                if (node.Rotation != 0)
                    sb.Append($" rot={node.Rotation:F1}");

                if (node.Type == NodeType.Shape && node.Bounds.width > 0)
                    sb.Append($" bounds={node.Bounds.width:F0}x{node.Bounds.height:F0}");

                if (node.Type == NodeType.Sprite)
                    sb.Append($" frames={node.FrameCount}({node.ParsedFrameCount})");

                if (node.HasExportedImage) sb.Append(" [IMG]");
                if (node.HasExportedSprite) sb.Append(" [SPR]");

                if (node.HasColorTransform)
                    sb.Append($" color=({node.ColorMultiply.r:F2},{node.ColorMultiply.g:F2},{node.ColorMultiply.b:F2},{node.ColorMultiply.a:F2})");

                if (!string.IsNullOrEmpty(node.Note))
                    sb.Append($" {node.Note}");

                sb.AppendLine();

                if (node.FrameLabels != null && node.FrameLabels.Count > 0)
                    sb.AppendLine($"{pad}  labels: {string.Join(", ", node.FrameLabels)}");

                if (node.Children != null)
                    DumpTree(sb, node.Children, indent + 1);
            }
        }

        int CountNodes(List<TreeNode> nodes)
        {
            int count = 0;
            foreach (var n in nodes)
            {
                count++;
                if (n.Children != null) count += CountNodes(n.Children);
            }
            return count;
        }

        void ExportToFile()
        {
            string path = EditorUtility.SaveFilePanel("Export Tree Dump",
                Application.dataPath, $"mainmenu_symbol{_exploreSymbolId}_tree", "txt");
            if (!string.IsNullOrEmpty(path))
            {
                File.WriteAllText(path, _rawDump);
                Debug.Log($"[MainMenuSWFExplorer] Exported to {path}");
            }
        }

        void OnDestroy()
        {
            foreach (var tex in _imageCache.Values)
            {
                if (tex != null)
                    DestroyImmediate(tex);
            }
            _imageCache.Clear();
        }

        // ─── Tree data ──────────────────────────────────────────────

        enum NodeType { Shape, Sprite, Unknown }

        class TreeNode
        {
            public int SymbolId;
            public string InstanceName;
            public int Depth;
            public Vector2 Position;
            public float Rotation;
            public Vector2 Scale;
            public NodeType Type;
            public Rect Bounds;
            public int FrameCount;
            public int ParsedFrameCount;
            public bool HasExportedImage;
            public bool HasExportedSprite;
            public bool HasColorTransform;
            public Color ColorMultiply;
            public Color ColorAdd;
            public string Note;
            public List<string> FrameLabels;
            public List<TreeNode> Children;
        }
    }
}
#endif
