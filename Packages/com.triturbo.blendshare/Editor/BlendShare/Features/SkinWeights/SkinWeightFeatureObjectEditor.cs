using System;
using System.Linq;
using Triturbo.BlendShapeShare;
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
        public override string DisplayName => Localization.FeatureName(FeatureId);

        public override VisualElement CreateElement(MeshFeatureEditorContext context)
        {
            return SkinWeightFeatureEditorElement.Create(context.Feature as SkinWeightFeatureObject);
        }
    }



    public sealed class SkinWeightFeatureEditorFactory : IMeshFeatureObjectEditor
    {
        public string FeatureId => SkinWeightFeatureObject.Id;
        public string DisplayName => Localization.FeatureName(FeatureId);
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
            BlendShareInspectorUi.StyleStrong(title);
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
                root.Add(new HelpBox(Localization.S("features.skin-weights.missing_data"), HelpBoxMessageType.Warning));
                return root;
            }

            root.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.clusters"), feature.ClusterCount.ToString()));
            root.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.weighted_control_points"), feature.WeightedControlPointCount.ToString()));
            root.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.root_bone"), feature.RootBonePath));
            root.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.bindposes"), feature.Clusters.Count(cluster => cluster != null && cluster.m_HasFbxClusterMatrices).ToString()));
            root.Add(CreateArmatureObjectRow(feature.Armature));
            root.Add(CreateClusterFoldout(feature, new SerializedObject(feature)));
            return root;
        }

        private static VisualElement CreateArmatureObjectRow(FbxArmatureObject armature)
        {
            var field = BlendShareInspectorUi.RowField(new ObjectField
            {
                objectType = typeof(FbxArmatureObject),
                allowSceneObjects = false,
                value = armature
            });
            field.SetEnabled(false);
            return BlendShareInspectorUi.LabeledRow(Localization.S("features.skin-weights.shared_armature"), field);
        }

        private static VisualElement CreateClusterFoldout(SkinWeightFeatureObject feature, SerializedObject serializedFeature)
        {
            var clusters = feature.Clusters;
            var clustersProperty = serializedFeature.FindProperty(nameof(SkinWeightFeatureObject.m_Clusters));
            var foldout = new Foldout
            {
                text = Localization.S("features.skin-weights.clusters"),
                value = false
            };
            foldout.style.paddingLeft = 10;

            if (clusters.Count == 0)
            {
                foldout.Add(new HelpBox(Localization.S("features.skin-weights.no_clusters"), HelpBoxMessageType.Info));
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
                item.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.bone"), cluster.BonePath));
                item.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.weights"), cluster.WeightCount.ToString()));
                item.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.fbx_cluster_matrices"), cluster.m_HasFbxClusterMatrices ? Localization.S("common.yes") : Localization.S("common.no")));
                if (cluster.m_HasFbxClusterMatrices)
                {
                    var clusterProperty = clustersProperty != null && i < clustersProperty.arraySize
                        ? clustersProperty.GetArrayElementAtIndex(i)
                        : null;
                    item.Add(CreateMatrixPropertyField(serializedFeature, clusterProperty, nameof(SkinWeightClusterData.m_FbxTransformMatrix), Localization.S("features.skin-weights.transform_matrix")));
                    item.Add(CreateMatrixPropertyField(serializedFeature, clusterProperty, nameof(SkinWeightClusterData.m_FbxTransformLinkMatrix), Localization.S("features.skin-weights.transform_link_matrix")));
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
                return new HelpBox(Localization.SF("features.skin-weights.matrix_data_missing", label), HelpBoxMessageType.Warning);
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
