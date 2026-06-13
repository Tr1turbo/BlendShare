using System;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.SkinWeights;
using Triturbo.BlendShare.Inspector;
using UnityEditor;
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

            root.Add(BlendShareInspectorUi.Row("Bone Slots", feature.BoneSlotCount.ToString()));
            root.Add(BlendShareInspectorUi.Row("Weighted Points", feature.WeightedControlPointCount.ToString()));
            root.Add(BlendShareInspectorUi.Row("Root Bone", feature.m_RootBonePath));
            root.Add(BlendShareInspectorUi.Row("Bind Poses", feature.BindPoses.Count.ToString()));
            root.Add(BlendShareInspectorUi.Row("Shared Bone Graph", feature.m_BoneGraph != null ? feature.m_BoneGraph.name : "None"));
            return root;
        }
    }
}
