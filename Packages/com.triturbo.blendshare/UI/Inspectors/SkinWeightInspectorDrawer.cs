using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Inspector;
using Triturbo.BlendShare.Model.Features;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.UI.Inspectors
{
    /// <summary>
    /// Verbatim port of the original <c>SkinWeightFeatureObjectEditor</c> / <c>SkinWeightFeatureEditorFactory</c>
    /// UI wired to BlendShare 2.0 <see cref="SkinWeightFeature"/> (preserving lazy payload discipline).
    /// </summary>
    public sealed class SkinWeightInspectorDrawer : IFeatureInspectorDrawer
    {
        public string FeatureTypeId => SkinWeightFeature.FeatureTypeId;
        public int DisplayOrder => 20;

        private static string DisplayName => Localization.FeatureName("skin-weights");

        public VisualElement CreateInspectorElement(BlendShareFeatureData data, FeatureInspectorContext context)
        {
            var box = BlendShareInspectorUi.Box();
            var title = new Label(DisplayName);
            BlendShareInspectorUi.StyleStrong(title);
            title.style.marginBottom = 4;
            box.Add(title);

            var feature = data as SkinWeightFeature;
            if (feature == null)
            {
                box.Add(new HelpBox(Localization.S("features.skin-weights.missing_data"), HelpBoxMessageType.Warning));
                return box;
            }

            var armature = context.Armatures.FirstOrDefault(a => a != null && string.Equals(a.ArmatureId, feature.ArmatureId, StringComparison.Ordinal));
            var clusters = feature.Clusters.Where(c => c != null).ToList();
            int affectedBoneCount = clusters
                .Select(c => c.BonePath)
                .Concat((armature?.Bones ?? (IEnumerable<Model.Documents.ArmatureBoneRecord>)Array.Empty<Model.Documents.ArmatureBoneRecord>()).Where(b => b != null).Select(b => b.Path))
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.Ordinal)
                .Count();

            var countButton = BlendShareInspectorUi.InlineButton(Localization.S("common.expand"), null);
            countButton.clicked += () =>
            {
                var vertices = new HashSet<int>();
                foreach (var cluster in clusters)
                {
                    if (cluster.Payload?.HasPayload != true) continue;
                    using var lease = cluster.Payload.Acquire(context.PayloadSource);
                    if (lease.Value?.DeltaWeights != null)
                    {
                        foreach (int index in lease.Value.DeltaWeights.Indices)
                        {
                            vertices.Add(index);
                        }
                    }
                }

                int totalControlPoints = context.MeshHeader?.Topology?.ControlPointCount ?? -1;
                countButton.text = totalControlPoints >= 0
                    ? Localization.SF("features.skin-weights.affected_vertices_value", vertices.Count, totalControlPoints)
                    : vertices.Count.ToString();
                countButton.SetEnabled(false);
            };

            box.Add(BlendShareInspectorUi.LabeledRow(Localization.S("features.skin-weights.affected_vertices"), countButton));
            box.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.affected_bones"), affectedBoneCount.ToString()));
            box.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.bindposes"), clusters.Count(c => c.HasClusterMatrices).ToString()));
            box.Add(BlendShareInspectorUi.Row(Localization.S("features.skin-weights.root_bone"), feature.RootBonePath));

            if (armature != null)
            {
                var armatureFoldout = new Foldout
                {
                    text = $"{Localization.S("features.skin-weights.shared_armature")} ({armature.ArmatureId})",
                    value = true
                };
                armatureFoldout.style.marginTop = 4;
                armatureFoldout.Add(new ArmatureTreeView(armature, context));
                box.Add(armatureFoldout);
            }

            box.Add(context.Advanced(feature));
            return box;
        }

        public VisualElement CreateCompactBadgeElement(BlendShareFeatureData data, FeatureInspectorContext context)
        {
            return new BlendShareFeatureBadge(DisplayName);
        }
    }
}
