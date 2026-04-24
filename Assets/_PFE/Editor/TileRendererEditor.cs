#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using PFE.Systems.Map.Rendering;

namespace PFE.Editor
{
    [CustomEditor(typeof(TileRenderer))]
    public class TileRendererEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            TileRenderer renderer = (TileRenderer)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Preview Debug", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(renderer == null))
            {
                if (GUILayout.Button("Apply Tile Edits + Rerender"))
                {
                    Undo.RecordObject(renderer, "Apply Tile Edits");
                    renderer.ApplyEditedPreviewTileAndRerender();
                    EditorUtility.SetDirty(renderer);
                }
            }
        }
    }
}
#endif
