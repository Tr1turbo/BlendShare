using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.Core
{
    public interface IBlendShareGenerationSource
    {
        Object TargetMeshContainer { get; }
        Transform TargetRoot { get; }
        IReadOnlyList<BlendShareObject> BlendShares { get; }
        IReadOnlyList<BlendShareGenerationRequest> Requests { get; }
        IReadOnlyList<string> Diagnostics { get; }
    }

    public sealed class BlendShareGenerationRequest
    {
        private readonly Dictionary<string, BlendShareGenerationBoneOverride> boneOverridesByPath;

        public int Order { get; }
        public BlendShareObject Share { get; }
        public MeshDataObject MeshData { get; }
        public string RendererNodePath { get; }
        public SkinnedMeshRenderer TargetRenderer { get; }
        public IReadOnlyList<BlendShareGenerationBoneOverride> BoneOverrides { get; }
        public IReadOnlyList<UnityVertexMappingObject> MappingOverrides { get; }

        public BlendShareGenerationRequest(
            int order,
            BlendShareObject share,
            MeshDataObject meshData,
            string rendererNodePath = null,
            SkinnedMeshRenderer targetRenderer = null,
            IEnumerable<BlendShareGenerationBoneOverride> boneOverrides = null,
            IEnumerable<UnityVertexMappingObject> mappingOverrides = null)
        {
            Order = order;
            Share = share;
            MeshData = meshData;
            RendererNodePath = MeshNodePath.Normalize(rendererNodePath ?? meshData?.m_Path);
            TargetRenderer = targetRenderer;
            BoneOverrides = (boneOverrides ?? Enumerable.Empty<BlendShareGenerationBoneOverride>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.SourceBonePath))
                .GroupBy(item => item.SourceBonePath)
                .Select(group => group.First())
                .ToArray();
            MappingOverrides = (mappingOverrides ?? Enumerable.Empty<UnityVertexMappingObject>())
                .Where(item => item != null)
                .ToArray();
            boneOverridesByPath = BoneOverrides.ToDictionary(item => item.SourceBonePath, item => item);
        }

        public UnityVertexMappingObject GetMappingFor(Mesh targetMesh)
        {
            return (MappingOverrides ?? Array.Empty<UnityVertexMappingObject>())
                       .FirstOrDefault(mapping => mapping != null && mapping.IsCompatibleWith(MeshData, targetMesh)) ??
                   (MeshData?.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                       .FirstOrDefault(mapping => mapping != null && mapping.IsCompatibleWith(MeshData, targetMesh));
        }

        public bool TryGetBoneOverride(string sourceBonePath, out BlendShareGenerationBoneOverride boneOverride)
        {
            return boneOverridesByPath.TryGetValue(MeshNodePath.Normalize(sourceBonePath), out boneOverride);
        }
    }

    public sealed class BlendShareGenerationBoneOverride
    {
        public string SourceBonePath { get; }
        public string FinalBonePath { get; }
        public string ParentPath { get; }
        public string BoneName { get; }
        public Transform TargetParent { get; }
        public Transform ProxyTransform { get; }
        public Vector3 LocalPosition { get; }
        public Vector3 LocalEulerRotation { get; }
        public Vector3 LocalScale { get; }
        public Vector3 GeneratedLocalPosition { get; }
        public Vector3 GeneratedLocalEulerRotation { get; }
        public Vector3 GeneratedLocalScale { get; }
        public bool RecalculateBindpose { get; }

        public BlendShareGenerationBoneOverride(
            string sourceBonePath,
            string finalBonePath,
            string parentPath,
            string boneName,
            Transform targetParent,
            Transform proxyTransform,
            Vector3 localPosition,
            Vector3 localEulerRotation,
            Vector3 localScale,
            Vector3 generatedLocalPosition,
            Vector3 generatedLocalEulerRotation,
            Vector3 generatedLocalScale,
            bool recalculateBindpose)
        {
            SourceBonePath = MeshNodePath.Normalize(sourceBonePath);
            FinalBonePath = MeshNodePath.Normalize(finalBonePath);
            ParentPath = MeshNodePath.Normalize(parentPath);
            BoneName = string.IsNullOrWhiteSpace(boneName) ? MeshNodePath.LeafName(FinalBonePath) : boneName;
            TargetParent = targetParent;
            ProxyTransform = proxyTransform;
            LocalPosition = localPosition;
            LocalEulerRotation = localEulerRotation;
            LocalScale = localScale == Vector3.zero ? Vector3.one : localScale;
            GeneratedLocalPosition = generatedLocalPosition;
            GeneratedLocalEulerRotation = generatedLocalEulerRotation;
            GeneratedLocalScale = generatedLocalScale == Vector3.zero ? Vector3.one : generatedLocalScale;
            RecalculateBindpose = recalculateBindpose;
        }

        public bool TryGetLocalToWorld(Transform fallbackRoot, out Matrix4x4 localToWorld)
        {
            var parent = TargetParent != null ? TargetParent : fallbackRoot;
            if (parent == null)
            {
                localToWorld = Matrix4x4.identity;
                return false;
            }

            localToWorld = parent.localToWorldMatrix * Matrix4x4.TRS(
                LocalPosition,
                Quaternion.Euler(LocalEulerRotation),
                LocalScale);
            return true;
        }
    }

    public sealed class BlendShareObjectGenerationSource : IBlendShareGenerationSource
    {
        private readonly List<string> diagnostics = new();

        public Object TargetMeshContainer { get; }
        public Transform TargetRoot { get; }
        public IReadOnlyList<BlendShareObject> BlendShares { get; }
        public IReadOnlyList<BlendShareGenerationRequest> Requests { get; }
        public IReadOnlyList<string> Diagnostics => diagnostics;

        public BlendShareObjectGenerationSource(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> blendShares,
            Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh = null)
        {
            TargetMeshContainer = targetMeshContainer;
            TargetRoot = targetMeshContainer is GameObject gameObject ? gameObject.transform : null;
            BlendShares = DedupBlendShares(blendShares).ToArray();
            Requests = BuildRequests(BlendShares, shouldGenerateMesh).ToArray();
        }

        private static IEnumerable<BlendShareGenerationRequest> BuildRequests(
            IEnumerable<BlendShareObject> blendShares,
            Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh)
        {
            int order = 0;
            foreach (var share in blendShares ?? Enumerable.Empty<BlendShareObject>())
            {
                foreach (var meshData in share.Meshes ?? Array.Empty<MeshDataObject>())
                {
                    if (meshData == null || (shouldGenerateMesh != null && !shouldGenerateMesh(share, meshData)))
                    {
                        continue;
                    }

                    yield return new BlendShareGenerationRequest(order++, share, meshData);
                }
            }
        }

        internal static IEnumerable<BlendShareObject> DedupBlendShares(IEnumerable<BlendShareObject> blendShares)
        {
            var seen = new HashSet<BlendShareObject>();
            foreach (var share in blendShares ?? Enumerable.Empty<BlendShareObject>())
            {
                if (share != null && seen.Add(share))
                {
                    yield return share;
                }
            }
        }
    }

}
