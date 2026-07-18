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
            BlendShareMesh applier,
            SkinnedMeshRenderer renderer)
        {
            ApplyWeightsToRenderer(applier?.BlendShapeWeights, renderer);
        }

        private static void ApplyWeightsToRenderer(
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

        internal static IReadOnlyDictionary<SkinnedMeshRenderer, IReadOnlyDictionary<string, float>>
            CaptureExistingRendererWeights(IEnumerable<BlendShareMesh> appliers)
        {
            var result = new Dictionary<SkinnedMeshRenderer, IReadOnlyDictionary<string, float>>();
            foreach (var renderer in (appliers ?? Enumerable.Empty<BlendShareMesh>())
                         .Select(applier => applier != null ? applier.TargetRenderer : null)
                         .Where(renderer => renderer != null)
                         .Distinct())
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                var weights = new Dictionary<string, float>();
                for (int index = 0; index < mesh.blendShapeCount; index++)
                {
                    string shapeName = mesh.GetBlendShapeName(index);
                    if (!string.IsNullOrWhiteSpace(shapeName) && !weights.ContainsKey(shapeName))
                    {
                        weights.Add(shapeName, renderer.GetBlendShapeWeight(index));
                    }
                }

                result.Add(renderer, weights);
            }

            return result;
        }

        internal static void ApplyCapturedRendererWeights(
            IReadOnlyDictionary<SkinnedMeshRenderer, IReadOnlyDictionary<string, float>> originalWeights,
            SkinnedMeshRenderer renderer)
        {
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (mesh == null ||
                originalWeights == null ||
                !originalWeights.TryGetValue(renderer, out var rendererWeights) ||
                rendererWeights == null)
            {
                return;
            }

            foreach (var pair in rendererWeights)
            {
                int index = mesh.GetBlendShapeIndex(pair.Key);
                if (index >= 0)
                {
                    renderer.SetBlendShapeWeight(index, pair.Value);
                }
            }
        }

        public static void RetargetRendererBlendShapeCurves(
            IEnumerable<BlendShareMesh> appliers,
            AnimatorServicesContext animatorServices)
        {
            if (animatorServices == null)
            {
                return;
            }

            var remaps = (appliers ?? Enumerable.Empty<BlendShareMesh>())
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
                    var rendererControlledBindings = remaps
                        .Select(remap => remap.TargetBinding)
                        .Distinct()
                        .Where(binding => clip.GetFloatCurve(binding) != null)
                        .ToHashSet();
                    foreach (var remap in remaps)
                    {
                        var sourceCurve = clip.GetFloatCurve(remap.SourceBinding);
                        if (sourceCurve == null)
                        {
                            continue;
                        }

                        if (!rendererControlledBindings.Contains(remap.TargetBinding))
                        {
                            // Later hierarchy entries overwrite earlier component curves.
                            clip.SetFloatCurve(remap.TargetBinding, CopyCurve(sourceCurve));
                        }

                        clip.SetFloatCurve(remap.SourceBinding, null);
                    }
                });
        }

        private static IEnumerable<CurveRemap> BuildRendererCurveRemaps(
            BlendShareMesh applier,
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
                    typeof(BlendShareMesh),
                    propertyName);
                if (!sourceBinding.Equals(targetBinding))
                {
                    yield return new CurveRemap(sourceBinding, targetBinding);
                }
            }
        }

        private static IEnumerable<string> GetBlendShapeNames(BlendShareMesh applier)
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
