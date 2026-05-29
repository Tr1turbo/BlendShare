using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    internal static class BlendShareBlendShapeWeightService
    {
        private const string RendererBlendShapePropertyPrefix = "blendShape.";

        public static void ApplyWeightsToRenderer(
            BlendShareMeshComponent applier,
            SkinnedMeshRenderer renderer)
        {
            ApplyWeightsToRenderer(applier?.BlendShapeWeights, renderer);
        }

        public static void ApplyWeightsToRenderer(
            IEnumerable<BlendShareProxyBlendShapeWeight> weights,
            SkinnedMeshRenderer renderer)
        {
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (mesh == null)
            {
                return;
            }

            foreach (var weight in weights ?? Enumerable.Empty<BlendShareProxyBlendShapeWeight>())
            {
                if (weight == null || string.IsNullOrWhiteSpace(weight.ShapeName))
                {
                    continue;
                }

                int index = mesh.GetBlendShapeIndex(weight.ShapeName);
                if (index >= 0)
                {
                    renderer.SetBlendShapeWeight(index, weight.Weight);
                }
            }
        }

        public static void RetargetRendererBlendShapeCurves(
            IEnumerable<BlendShareMeshComponent> appliers,
            AnimatorServicesContext animatorServices)
        {
            if (animatorServices == null)
            {
                return;
            }

            var remaps = (appliers ?? Enumerable.Empty<BlendShareMeshComponent>())
                .Where(applier => applier != null &&
                                  applier.TargetRenderer != null)
                .SelectMany(applier => BuildRendererCurveRemaps(applier, animatorServices.ObjectPathRemapper))
                .ToArray();
            if (remaps.Length == 0)
            {
                return;
            }

            animatorServices.AnimationIndex.EditClipsByBinding(
                remaps.Select(remap => remap.SourceBinding),
                clip =>
                {
                    foreach (var remap in remaps)
                    {
                        var sourceCurve = clip.GetFloatCurve(remap.SourceBinding);
                        if (sourceCurve == null)
                        {
                            continue;
                        }

                        clip.SetFloatCurve(remap.TargetBinding, CopyCurve(sourceCurve));
                        clip.SetFloatCurve(remap.SourceBinding, null);
                    }
                });
        }

        private static IEnumerable<CurveRemap> BuildRendererCurveRemaps(
            BlendShareMeshComponent applier,
            ObjectPathRemapper pathRemapper)
        {
            if (applier == null || applier.TargetRenderer == null)
            {
                yield break;
            }

            string targetPath = pathRemapper.GetVirtualPathForObject(applier.TargetRenderer.gameObject);
            pathRemapper?.RecordObjectTree(applier.transform);
            string applierPath = pathRemapper.GetVirtualPathForObject(applier.gameObject);

            foreach (string shapeName in GetBlendShapeNames(applier).Distinct())
            {
                if (string.IsNullOrWhiteSpace(shapeName))
                {
                    continue;
                }

                string propertyName = $"{RendererBlendShapePropertyPrefix}{shapeName}";
                var targetBinding = EditorCurveBinding.FloatCurve(
                    targetPath,
                    typeof(SkinnedMeshRenderer),
                    propertyName);
                var sourceBinding = EditorCurveBinding.FloatCurve(
                    applierPath,
                    typeof(BlendShareMeshComponent),
                    propertyName);
                if (!sourceBinding.Equals(targetBinding))
                {
                    yield return new CurveRemap(sourceBinding, targetBinding);
                }
            }
        }

        private static IEnumerable<string> GetBlendShapeNames(BlendShareMeshComponent applier)
        {
            foreach (var weight in applier?.BlendShapeWeights ?? System.Array.Empty<BlendShareProxyBlendShapeWeight>())
            {
                if (weight != null)
                {
                    yield return weight.ShapeName;
                }
            }
        }

        private static AnimationCurve CopyCurve(AnimationCurve curve)
        {
            var copy = new AnimationCurve(curve.keys)
            {
                preWrapMode = curve.preWrapMode,
                postWrapMode = curve.postWrapMode
            };
            return copy;
        }

        private readonly struct CurveRemap
        {
            public CurveRemap(EditorCurveBinding sourceBinding, EditorCurveBinding targetBinding)
            {
                SourceBinding = sourceBinding;
                TargetBinding = targetBinding;
            }

            public EditorCurveBinding SourceBinding { get; }
            public EditorCurveBinding TargetBinding { get; }
        }
    }
}
