using Triturbo.BlendShare.Model.Features;
using UnityEditor;

namespace Triturbo.BlendShare.UI.Inspectors
{
    /// <summary>
    /// Inspector drawer for SkinWeightFeature displaying armature identity and bone cluster summary.
    /// </summary>
    public sealed class SkinWeightInspectorDrawer : IFeatureInspectorDrawer
    {
        public string FeatureTypeId => SkinWeightFeature.FeatureTypeId;
        public int DisplayOrder => 20;

        public void DrawInspector(BlendShareFeatureData featureData, FeatureInspectorContext context)
        {
            if (!(featureData is SkinWeightFeature feature)) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Skin Weights", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Armature ID", string.IsNullOrEmpty(feature.ArmatureId) ? "(none)" : feature.ArmatureId);
            EditorGUILayout.LabelField("Root Bone", string.IsNullOrEmpty(feature.RootBonePath) ? "(root)" : feature.RootBonePath);
            int clusterCount = feature.Clusters != null ? feature.Clusters.Count : 0;
            EditorGUILayout.LabelField("Bone Clusters", clusterCount.ToString());
            EditorGUILayout.EndVertical();
        }
    }
}
