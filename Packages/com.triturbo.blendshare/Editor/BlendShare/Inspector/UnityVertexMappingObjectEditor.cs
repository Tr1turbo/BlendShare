using System;
using Triturbo.BlendShapeShare;
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
            var mesh = BlendShareInspectorUtility.FindOwnerMesh(mapping);
            var patch = BlendShareInspectorUtility.FindOwnerPatch(mapping);

            void Rebuild()
            {
                root.Clear();
                var embeddedEditor = new UnityVertexMappingEmbeddedEditor();
                root.Add(BlendShareInspectorUi.Header(embeddedEditor.DisplayName));
                root.Add(embeddedEditor.CreateEmbeddedInspector(new BlendShareEmbeddedEditorContext(
                    mapping,
                    mesh,
                    patch,
                    null)));

                var advanced = new Foldout { text = Localization.S("common.advanced") };
                advanced.Add(BlendShareInspectorUi.Row(Localization.S("patch.mapping.fbx_to_unity_scale"), mapping != null ? mapping.FbxToUnityScale.ToString("0.###") : "-"));
                advanced.Add(BlendShareInspectorUi.Row(Localization.S("patch.mapping.bake_axis_conversion"), mapping != null && mapping.m_BakeAxisConversion ? Localization.S("common.yes") : Localization.S("common.no")));
                advanced.Add(BlendShareInspectorUi.Row(Localization.S("patch.mapping.index_entries"), mapping?.m_Indices != null ? mapping.m_Indices.Length.ToString() : "0"));
                advanced.Add(BlendShareInspectorUi.Row(Localization.S("patch.mapping.grouped_entries"), mapping?.m_IndexGroups != null ? mapping.m_IndexGroups.Length.ToString() : "0"));
                root.Add(advanced);
            }

            Rebuild();
            Localization.RebuildOnLanguageChange(root, Rebuild);
            return root;
        }
    }

    public sealed class UnityVertexMappingEmbeddedEditor : IBlendShareEmbeddedEditor
    {
        public Type TargetType => typeof(UnityVertexMappingObject);
        public string DisplayName => Localization.S("patch.mapping.display_name");

        public VisualElement CreateEmbeddedInspector(BlendShareEmbeddedEditorContext context)
        {
            var mapping = context.EmbeddedObject as UnityVertexMappingObject;
            var root = BlendShareInspectorUi.Box();
            if (mapping == null)
            {
                root.Add(new HelpBox(Localization.S("patch.mapping.missing"), HelpBoxMessageType.Warning));
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


            root.Add(BlendShareInspectorUi.Row(Localization.S("patch.mapping.vertex_hash"), mapping.UnityVerticesHashShort));
            root.Add(BlendShareInspectorUi.Row(Localization.S("common.status"), mapping.m_IsValid ? Localization.S("common.status.valid") : Localization.S("common.status.invalid")));
            root.Add(BlendShareInspectorUi.Row(Localization.S("patch.mapping.unity_vertices"), mapping.m_UnityVertexCount.ToString()));
            root.Add(BlendShareInspectorUi.LabeledRow(Localization.S("patch.mapping.unity_mesh"), meshField));

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
