using System;
using Triturbo.BlendShare.Core;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Inspector
{
    public interface IMeshFeatureObjectEditor : IBlendShareEmbeddedEditor
    {
        string FeatureId { get; }
        VisualElement CreateElement(MeshFeatureEditorContext context);
        VisualElement CreateCompactElement(MeshFeatureEditorContext context);
        //long EstimateVideoMemoryBytes(MeshFeatureEditorContext context, int unityVertexCount);
    }

    public abstract class MeshFeatureObjectEditor<TFeature> : UnityEditor.Editor, IMeshFeatureObjectEditor, IBlendShareEmbeddedEditor
        where TFeature : MeshFeatureObject
    {
        public abstract string FeatureId { get; }
        public abstract string DisplayName { get; }
        public Type TargetType => typeof(TFeature);

        public override VisualElement CreateInspectorGUI()
        {
            var feature = target as TFeature;
            var root = BlendShareInspectorUi.CreateRoot();
            root.Add(BlendShareInspectorUi.Header(DisplayName));

            root.Add(CreateEmbeddedInspector(new BlendShareEmbeddedEditorContext(
                feature,
                BlendShareInspectorUtility.FindOwnerMesh(feature),
                BlendShareInspectorUtility.FindOwnerPatch(feature),
                null)));
            return root;
        }

        public VisualElement CreateEmbeddedInspector(BlendShareEmbeddedEditorContext context)
        {
            return CreateElement(new MeshFeatureEditorContext(
                context.EmbeddedObject as MeshFeatureObject,
                context.OwnerMeshData,
                context.OwnerPatch,
                context.Refresh));
        }

        public virtual VisualElement CreateCompactElement(MeshFeatureEditorContext context)
        {
            return new BlendShareFeatureBadge(DisplayName);
        }

        // public virtual long EstimateVideoMemoryBytes(MeshFeatureEditorContext context, int unityVertexCount)
        // {
        //     return 0;
        // }

        public abstract VisualElement CreateElement(MeshFeatureEditorContext context);
    }
}
