using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Inspector;
using Triturbo.BlendShare.Model.Features;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.UI.Inspectors
{
    public sealed class SkinWeightInspectorDrawer : IFeatureInspectorDrawer
    {
        public string FeatureTypeId => SkinWeightFeature.FeatureTypeId;
        public int DisplayOrder => 20;

        public VisualElement CreateInspectorElement(BlendShareFeatureData data, FeatureInspectorContext context)
        {
            var feature = (SkinWeightFeature)data;
            var box = BlendShareInspectorUi.Box();
            box.Add(BlendShareInspectorUi.Header(Localization.FeatureName("skin-weights")));
            var armature = context.Armatures.FirstOrDefault(a => a?.ArmatureId == feature.ArmatureId);
            var clusters = feature.Clusters.Where(c => c != null).ToList();
            var paths = clusters.Select(c => c.BonePath).Concat(armature?.Bones.Where(b => b != null)
                .Select(b => b.Path) ?? Enumerable.Empty<string>());
            // Distinct affected vertices span cluster payloads and cannot be counted from the manifest.
            var count = new Button { text = Localization.S("common.expand") };
            count.clicked += () =>
            {
                var vertices = new HashSet<int>();
                foreach (var cluster in clusters)
                {
                    if (cluster.Payload?.HasPayload != true) continue;
                    using var lease = cluster.Payload.Acquire(context.PayloadSource);
                    if (lease.Value?.DeltaWeights != null)
                        foreach (int index in lease.Value.DeltaWeights.Indices) vertices.Add(index);
                }
                count.text = Localization.SF("features.skin-weights.affected_vertices_value", vertices.Count,
                    context.MeshHeader?.Topology?.ControlPointCount ?? 0);
                count.SetEnabled(false);
            };
            box.Add(BlendShareInspectorUi.LabeledRow(Localization.S("features.skin-weights.affected_vertices"), count));
            box.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.affected_bones"), paths.Where(p => !string.IsNullOrEmpty(p)).Distinct().Count().ToString()));
            box.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.bindposes"), clusters.Count(c => c.HasClusterMatrices).ToString()));
            box.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.root_bone"), feature.RootBonePath));
            if (armature != null) box.Add(new ArmatureTreeView(armature, context));
            box.Add(context.Advanced(feature));
            return box;
        }

        public VisualElement CreateCompactBadgeElement(BlendShareFeatureData data, FeatureInspectorContext context) =>
            new BlendShareFeatureBadge(Localization.FeatureName("skin-weights"), CreateInspectorElement(data, context));
    }
}
