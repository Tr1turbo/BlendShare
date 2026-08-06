using System;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.SkinWeights;
using Triturbo.BlendShare.Inspector;
using UnityEditor;
using UnityEditor.UIElements;
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
            return SkinWeightFeatureEditorElement.Create(context);
        }
    }



    public sealed class SkinWeightFeatureEditorFactory : IMeshFeatureObjectEditor
    {
        public string FeatureId => SkinWeightFeatureObject.Id;
        public string DisplayName => Localization.FeatureName(FeatureId);
        public Type TargetType => typeof(SkinWeightFeatureObject);

        public VisualElement CreateElement(MeshFeatureEditorContext context)
        {
            return SkinWeightFeatureEditorElement.Create(context);
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
        public static VisualElement Create(MeshFeatureEditorContext context)
        {
            var root = new VisualElement();
            var feature = context?.Feature as SkinWeightFeatureObject;
            if (feature == null)
            {
                root.Add(new HelpBox(Localization.S("features.skin-weights.missing_data"), HelpBoxMessageType.Warning));
                return root;
            }

            var ownerMesh = context.OwnerMesh ?? BlendShareInspectorUtility.FindOwnerMesh(feature, context.OwnerPatch);
            int fbxVertexCount = ownerMesh?.FbxControlPointCount ?? -1;
            string affectedVertices = fbxVertexCount >= 0
                ? Localization.SF("features.skin-weights.affected_vertices_value", feature.WeightedControlPointCount, fbxVertexCount)
                : feature.WeightedControlPointCount.ToString();
            int affectedBoneCount = feature.Clusters
                .Where(cluster => cluster != null)
                .Select(cluster => cluster.BonePath)
                .Concat((feature.Armature?.Bones ?? Array.Empty<FbxArmatureBoneData>())
                    .Where(bone => bone != null)
                    .Select(bone => bone.Path))
                .Where(path => path != MeshNodePath.Root)
                .Distinct(StringComparer.Ordinal)
                .Count();

            root.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.affected_vertices"), affectedVertices));
            root.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.affected_bones"), affectedBoneCount.ToString()));
            root.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.bindposes"), feature.Clusters.Count(cluster => cluster != null && cluster.m_HasFbxClusterMatrices).ToString()));
            root.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.root_bone"), feature.RootBonePath));
            root.Add(CreateArmatureObjectRow(feature.Armature));
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

    }
}
