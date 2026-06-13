using System;
using Triturbo.BlendShare.Core;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Inspector
{
    [CustomEditor(typeof(UnityVertexMappingObject))]
    public sealed class UnityVertexMappingObjectEditor : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var mapping = target as UnityVertexMappingObject;
            var root = BlendShareInspectorUi.CreateRoot();
            var embeddedEditor = new UnityVertexMappingEmbeddedEditor();
            var mesh = BlendShareInspectorUtility.FindOwnerMesh(mapping);
            var patch = BlendShareInspectorUtility.FindOwnerPatch(mapping);
            root.Add(BlendShareInspectorUi.Header(embeddedEditor.DisplayName));
            root.Add(embeddedEditor.CreateEmbeddedInspector(new BlendShareEmbeddedEditorContext(
                mapping,
                mesh,
                patch,
                null)));

            var advanced = new Foldout { text = "Advanced" };
            advanced.Add(BlendShareInspectorUi.Row("FBX to Unity Scale", mapping != null ? mapping.FbxToUnityScale.ToString("0.###") : "-"));
            advanced.Add(BlendShareInspectorUi.Row("Bake Axis Conversion", mapping != null && mapping.m_BakeAxisConversion ? "Yes" : "No"));
            advanced.Add(BlendShareInspectorUi.Row("Index Entries", mapping?.m_Indices != null ? mapping.m_Indices.Length.ToString() : "0"));
            advanced.Add(BlendShareInspectorUi.Row("Grouped Entries", mapping?.m_IndexGroups != null ? mapping.m_IndexGroups.Length.ToString() : "0"));
            root.Add(advanced);
            return root;
        }
    }

    public sealed class UnityVertexMappingEmbeddedEditor : IBlendShareEmbeddedEditor
    {
        public Type TargetType => typeof(UnityVertexMappingObject);
        public string DisplayName => "Unity Mapping";

        public VisualElement CreateEmbeddedInspector(BlendShareEmbeddedEditorContext context)
        {
            var mapping = context.EmbeddedObject as UnityVertexMappingObject;
            var root = BlendShareInspectorUi.Box();
            if (mapping == null)
            {
                root.Add(new HelpBox("Unity mapping data is missing.", HelpBoxMessageType.Warning));
                return root;
            }

            BlendShareInspectorUi.RegisterDoubleClickAction(root, () =>
            {
                Selection.activeObject = mapping;
                EditorGUIUtility.PingObject(mapping);
            });

            var meshField = new ObjectField
            {
                objectType = typeof(Mesh),
                allowSceneObjects = false,
                value = mapping.m_UnityMesh
            };
            meshField.SetEnabled(false);
            meshField.style.flexGrow = 1;
            meshField.style.flexShrink = 1;
            meshField.style.minWidth = 0;


            root.Add(BlendShareInspectorUi.Row("Vertex Hash", mapping.UnityVerticesHashShort));
            root.Add(BlendShareInspectorUi.Row("Status", mapping.m_IsValid ? "Valid" : "Invalid"));
            root.Add(BlendShareInspectorUi.Row("Unity Vertices", mapping.m_UnityVertexCount.ToString()));
            root.Add(BlendShareInspectorUi.LabeledRow("Unity Mesh", meshField));

            if (!string.IsNullOrWhiteSpace(mapping.m_Report))
            {
                BlendShareInspectorUi.AddHelpBox(
                    root,
                    mapping.m_Report,
                    mapping.m_IsValid ? HelpBoxMessageType.Info : HelpBoxMessageType.Warning);
            }

            return root;
        }
    }
}
