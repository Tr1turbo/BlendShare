using System;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.SkinWeights;
using Triturbo.BlendShare.Inspector;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Features.SkinWeights.Editor
{
    [CustomEditor(typeof(SkinWeightFeatureObject))]
    public sealed class SkinWeightFeatureObjectEditor : MeshFeatureObjectEditor<SkinWeightFeatureObject>
    {
        public override string FeatureId => SkinWeightFeatureObject.Id;
        public override string DisplayName => "Skin Weights";

        public override VisualElement CreateElement(MeshFeatureEditorContext context)
        {
            return SkinWeightFeatureEditorElement.Create(context.Feature as SkinWeightFeatureObject);
        }
    }



    public sealed class SkinWeightFeatureEditorFactory : IMeshFeatureObjectEditor
    {
        public string FeatureId => SkinWeightFeatureObject.Id;
        public string DisplayName => "Skin Weights";
        public Type TargetType => typeof(SkinWeightFeatureObject);

        public VisualElement CreateElement(MeshFeatureEditorContext context)
        {
            return SkinWeightFeatureEditorElement.Create(context.Feature as SkinWeightFeatureObject);
        }

        public VisualElement CreateCompactElement(MeshFeatureEditorContext context)
        {
            return new BlendShareFeatureBadge(DisplayName);
        }

        public long EstimateVideoMemoryBytes(MeshFeatureEditorContext context, int unityVertexCount)
        {
            return 0;
        }

        public VisualElement CreateEmbeddedInspector(BlendShareEmbeddedEditorContext context)
        {
            var box = BlendShareInspectorUi.Box();
            BlendShareInspectorUi.RegisterDoubleClickAction(box, () => Selection.activeObject = context.EmbeddedObject);
            var title = new Label(DisplayName);
            title.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Bold;
            title.style.marginBottom = 4;
            box.Add(title);
            box.Add(CreateElement(new MeshFeatureEditorContext(
                context.EmbeddedObject as MeshFeatureObject,
                context.OwnerMeshData,
                context.OwnerPatch,
                context.Refresh)));
            return box;
        }
    }

    internal static class SkinWeightFeatureEditorElement
    {
        public static VisualElement Create(SkinWeightFeatureObject feature)
        {
            var root = new VisualElement();
            if (feature == null)
            {
                root.Add(new HelpBox("Skin weight feature data is missing.", HelpBoxMessageType.Warning));
                return root;
            }

            root.Add(BlendShareInspectorUi.Row("Clusters", feature.ClusterCount.ToString()));
            root.Add(BlendShareInspectorUi.Row("Weighted Points", feature.WeightedControlPointCount.ToString()));
            root.Add(BlendShareInspectorUi.Row("Root Bone", feature.RootBonePath));
            root.Add(BlendShareInspectorUi.Row("Bind Poses", feature.Clusters.Count(cluster => cluster != null && cluster.m_HasFbxClusterMatrices).ToString()));
            root.Add(CreateArmatureObjectRow(feature.Armature));
            root.Add(CreateClusterFoldout(feature, new SerializedObject(feature)));
            return root;
        }

        private static VisualElement CreateArmatureObjectRow(ArmatureObject armature)
        {
            var field = new ObjectField
            {
                objectType = typeof(ArmatureObject),
                allowSceneObjects = false,
                value = armature
            };
            field.SetEnabled(false);
            field.style.flexGrow = 1;
            field.style.flexShrink = 1;
            field.style.minWidth = 0;
            return BlendShareInspectorUi.LabeledRow("Shared Armature", field);
        }

        private static VisualElement CreateClusterFoldout(SkinWeightFeatureObject feature, SerializedObject serializedFeature)
        {
            var clusters = feature.Clusters;
            var clustersProperty = serializedFeature.FindProperty(nameof(SkinWeightFeatureObject.m_Clusters));
            var foldout = new Foldout
            {
                text = "Clusters",
                value = false
            };
            foldout.style.paddingLeft = 10;

            if (clusters.Count == 0)
            {
                foldout.Add(new HelpBox("No clusters are stored for this feature.", HelpBoxMessageType.Info));
                return foldout;
            }

            for (int i = 0; i < clusters.Count; i++)
            {
                var cluster = clusters[i];
                if (cluster == null)
                {
                    continue;
                }

                var item = SkinWeightInspectorLayout.CreatePlainItem();
                item.Add(BlendShareInspectorUi.Row("Bone", cluster.BonePath));
                item.Add(BlendShareInspectorUi.Row("Weights", cluster.WeightCount.ToString()));
                item.Add(BlendShareInspectorUi.Row("FBX Cluster Matrices", cluster.m_HasFbxClusterMatrices ? "Yes" : "No"));
                if (cluster.m_HasFbxClusterMatrices)
                {
                    var clusterProperty = clustersProperty != null && i < clustersProperty.arraySize
                        ? clustersProperty.GetArrayElementAtIndex(i)
                        : null;
                    item.Add(CreateMatrixPropertyField(serializedFeature, clusterProperty, nameof(SkinWeightClusterData.m_FbxTransformMatrix), "Transform Matrix"));
                    item.Add(CreateMatrixPropertyField(serializedFeature, clusterProperty, nameof(SkinWeightClusterData.m_FbxTransformLinkMatrix), "Transform Link Matrix"));
                }
                foldout.Add(item);
            }

            return foldout;
        }

        private static VisualElement CreateMatrixPropertyField(
            SerializedObject serializedFeature,
            SerializedProperty clusterProperty,
            string propertyName,
            string label)
        {
            var matrixProperty = clusterProperty?.FindPropertyRelative(propertyName);
            if (matrixProperty == null)
            {
                return new HelpBox($"{label} data is missing.", HelpBoxMessageType.Warning);
            }

            var field = new PropertyField(matrixProperty, label);
            field.Bind(serializedFeature);
            return field;
        }

    }

    internal static class SkinWeightInspectorLayout
    {
        public static VisualElement CreatePlainItem()
        {
            var item = new VisualElement();
            item.style.borderTopWidth = 1;
            item.style.borderTopColor = new Color(0.35f, 0.35f, 0.35f, 0.55f);
            item.style.marginTop = 5;
            item.style.paddingTop = 5;
            item.style.paddingBottom = 3;
            return item;
        }
    }

}
