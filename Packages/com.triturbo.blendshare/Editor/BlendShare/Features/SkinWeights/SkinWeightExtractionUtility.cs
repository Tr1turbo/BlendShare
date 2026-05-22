using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;
using UnityEngine;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    internal static class SkinWeightExtractionUtility
    {
        public static SkinnedMeshRenderer GetSourceRenderer(MeshFeatureExtractionContext context)
        {
            var transform = MeshNodePath.FindRelativeTransform(
                context?.Session?.SourceFbxGo != null ? context.Session.SourceFbxGo.transform : null,
                context?.Path);
            return transform != null ? transform.GetComponent<SkinnedMeshRenderer>() : null;
        }

        public static SkinnedMeshRenderer GetOriginRenderer(MeshFeatureExtractionContext context)
        {
            var transform = MeshNodePath.FindRelativeTransform(
                context?.Session?.OriginFbxGo != null ? context.Session.OriginFbxGo.transform : null,
                context?.Path);
            return transform != null ? transform.GetComponent<SkinnedMeshRenderer>() : null;
        }

        public static UfbxSkinDeformer GetPrimarySkinDeformer(MeshFeatureExtractionContext context)
        {
            return GetPrimarySourceSkinDeformer(context);
        }

        public static UfbxSkinDeformer GetPrimarySourceSkinDeformer(MeshFeatureExtractionContext context)
        {
            return context?.GetSourceFbxMesh()?.SkinDeformers
                .FirstOrDefault(deformer => deformer != null && deformer.Clusters.Any(cluster => cluster.WeightCount > 0));
        }

        public static UfbxSkinDeformer GetPrimaryOriginSkinDeformer(MeshFeatureExtractionContext context)
        {
            return context?.GetOriginFbxMesh()?.SkinDeformers
                .FirstOrDefault(deformer => deformer != null && deformer.Clusters.Any(cluster => cluster.WeightCount > 0));
        }

        public static string NormalizeBonePath(string path)
        {
            return MeshNodePath.Normalize(path);
        }

        public static string GetTransformPath(Transform transform, Transform root)
        {
            return MeshNodePath.Normalize(transform != null && root != null
                ? MeshNodePath.GetRelativePath(transform, root)
                : null);
        }

        public static UfbxSkinCluster GetCluster(UfbxSkinDeformer skin, int boneIndex)
        {
            return skin != null && boneIndex >= 0 && boneIndex < skin.Clusters.Count
                ? skin.Clusters[boneIndex]
                : null;
        }

        public static Dictionary<string, Transform> BuildTransformLookup(Transform root)
        {
            var lookup = new Dictionary<string, Transform>();
            if (root == null)
            {
                return lookup;
            }

            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                string path = GetTransformPath(transform, root);
                if (!lookup.ContainsKey(path))
                {
                    lookup.Add(path, transform);
                }
            }

            return lookup;
        }
    }
}
