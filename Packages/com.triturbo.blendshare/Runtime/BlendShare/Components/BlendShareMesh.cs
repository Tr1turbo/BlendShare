using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BlendShapes;
using Triturbo.BlendShare.Features.SkinWeights;
using UnityEngine;
using UnityEngine.Animations;

namespace Triturbo.BlendShare.Components
{
    [Serializable]
    public sealed class BlendShareProxyBlendShapeWeight
    {
        public const string ShapeNameFieldName = "m_ShapeName";
        public const string WeightFieldName = "m_Weight";

        [SerializeField, NotKeyable]
        private string m_ShapeName;

        [SerializeField, Range(0f, 100f)]
        private float m_Weight;

        public string ShapeName
        {
            get => m_ShapeName ?? string.Empty;
            set => m_ShapeName = value ?? string.Empty;
        }

        public float Weight
        {
            get => m_Weight;
            set => m_Weight = Mathf.Clamp(value, 0f, 100f);
        }

        public BlendShareProxyBlendShapeWeight() { }

        public BlendShareProxyBlendShapeWeight(string shapeName, float weight)
        {
            ShapeName = shapeName;
            Weight = weight;
        }
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("BlendShare/BlendShare Mesh")]
    public sealed class BlendShareMesh : BlendShareComponent
    {
        public const string BlendShapeWeightsFieldName = "m_BlendShapeWeights";

        [SerializeField, NotKeyable]
        private BlendShareObject m_Patch;

        [SerializeField, NotKeyable]
        private AvatarObjectReference<SkinnedMeshRenderer> m_TargetRendererReference = new();

        [SerializeField, NotKeyable]
        private MeshDataObject m_MeshData;

        [SerializeField, NotKeyable]
        private UnityVertexMappingObject m_Mapping;

        [SerializeField, NotKeyable]
        private string m_RendererNodePath;

        [SerializeField]
        private List<BlendShareProxyBlendShapeWeight> m_BlendShapeWeights = new();

        [NonSerialized]
        private Dictionary<string, BlendShareBoneProxy> m_BoneProxiesBySourcePath = new(StringComparer.Ordinal);

        public BlendShareObject Patch
        {
            get => m_Patch;
            set => m_Patch = value;
        }

        public SkinnedMeshRenderer TargetRenderer
        {
            get => m_TargetRendererReference != null && m_TargetRendererReference.IsConfigured
                ? m_TargetRendererReference.Get(this)
                : null;
            set => EnsureTargetRendererReferenceInitialized().Set(value);
        }

        public MeshDataObject MeshData
        {
            get => m_MeshData;
            set => m_MeshData = value;
        }

        public UnityVertexMappingObject Mapping
        {
            get => m_Mapping;
            set => m_Mapping = value;
        }

        public string RendererNodePath
        {
            get => MeshNodePath.Normalize(m_RendererNodePath);
            set => m_RendererNodePath = MeshNodePath.Normalize(value);
        }

        public IReadOnlyList<BlendShareProxyBlendShapeWeight> BlendShapeWeights =>
            m_BlendShapeWeights ??= new List<BlendShareProxyBlendShapeWeight>();

        /// <summary>Gets the currently cached proxy components.</summary>
        public IReadOnlyCollection<BlendShareBoneProxy> CachedBoneProxies =>
            EnsureBoneProxyCache().Values
                .Where(proxy => proxy != null)
                .Distinct()
                .ToArray();

        /// <summary>Gets a cached proxy by required source bone path.</summary>
        public bool TryGetCachedBoneProxy(string sourceBonePath, out BlendShareBoneProxy proxy)
        {
            proxy = null;
            string normalizedPath = MeshNodePath.NormalizeOptional(sourceBonePath);
            return !string.IsNullOrEmpty(normalizedPath) &&
                   EnsureBoneProxyCache().TryGetValue(normalizedPath, out proxy) &&
                   proxy != null;
        }

        private void SetBoneProxyCache(
            IEnumerable<KeyValuePair<string, BlendShareBoneProxy>> references)
        {
            m_BoneProxiesBySourcePath = references?
                .Where(reference =>
                    !string.IsNullOrEmpty(MeshNodePath.NormalizeOptional(reference.Key)) &&
                    reference.Value != null)
                .GroupBy(reference => MeshNodePath.NormalizeOptional(reference.Key), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal) ??
                new Dictionary<string, BlendShareBoneProxy>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Refreshes transient proxy caches for the supplied meshes from one hierarchy-ordered proxy pass.
        /// </summary>
        public static void RefreshBoneProxyCaches(
            IEnumerable<BlendShareMesh> meshes,
            Transform targetRoot,
            IEnumerable<BlendShareBoneProxy> availableProxies)
        {
            var proxiesBySource = new Dictionary<SourceKey, BlendShareBoneProxy>();
            foreach (var proxy in (availableProxies ?? Enumerable.Empty<BlendShareBoneProxy>())
                         .Where(proxy => proxy != null && proxy.isActiveAndEnabled && proxy.SourceArmature != null)
                         .Distinct()
                         .OrderBy(proxy => GetHierarchyOrder(proxy.transform), StringComparer.Ordinal))
            {
                foreach (var binding in proxy.Bindings.Where(binding => binding?.IsConfigured == true))
                {
                    proxiesBySource[new SourceKey(proxy.SourceArmature, binding.SourceBonePath)] = proxy;
                }
            }

            foreach (var mesh in (meshes ?? Enumerable.Empty<BlendShareMesh>())
                         .Where(mesh => mesh != null)
                         .Distinct())
            {
                mesh.RefreshBoneProxyCache(targetRoot, proxiesBySource);
            }
        }

        /// <summary>Refreshes this mesh's transient proxy cache.</summary>
        public void RefreshBoneProxyCache(
            Transform targetRoot,
            IEnumerable<BlendShareBoneProxy> availableProxies)
        {
            RefreshBoneProxyCaches(new[] { this }, targetRoot, availableProxies);
        }

        private void RefreshBoneProxyCache(
            Transform targetRoot,
            IReadOnlyDictionary<SourceKey, BlendShareBoneProxy> proxiesBySource)
        {
            var skin = MeshData?.GetFeature<SkinWeightFeatureObject>();
            var renderer = TargetRenderer;
            if (targetRoot == null || renderer == null || skin?.Armature == null)
            {
                SetBoneProxyCache(Array.Empty<KeyValuePair<string, BlendShareBoneProxy>>());
                return;
            }

            var existingBonePaths = new HashSet<string>((renderer.bones ?? Array.Empty<Transform>())
                .Where(bone => bone != null)
                .Select(bone => MeshNodePath.Normalize(MeshNodePath.GetRelativePath(bone, targetRoot))));
            var references = new List<KeyValuePair<string, BlendShareBoneProxy>>();
            foreach (string sourceBonePath in skin.GetNeededBonePathsInArmatureOrder())
            {
                if (!existingBonePaths.Contains(sourceBonePath) &&
                    proxiesBySource != null &&
                    proxiesBySource.TryGetValue(new SourceKey(skin.Armature, sourceBonePath), out var proxy))
                {
                    references.Add(new KeyValuePair<string, BlendShareBoneProxy>(sourceBonePath, proxy));
                }
            }

            SetBoneProxyCache(references);
        }

        private Dictionary<string, BlendShareBoneProxy> EnsureBoneProxyCache()
        {
            return m_BoneProxiesBySourcePath ??= new Dictionary<string, BlendShareBoneProxy>(StringComparer.Ordinal);
        }

        private static string GetHierarchyOrder(Transform transform)
        {
            var indices = new Stack<string>();
            for (var current = transform; current != null; current = current.parent)
            {
                indices.Push(current.GetSiblingIndex().ToString("D8"));
            }

            return string.Join("/", indices);
        }

        private readonly struct SourceKey : IEquatable<SourceKey>
        {
            private readonly FbxArmatureObject armature;
            private readonly string sourceBonePath;

            public SourceKey(FbxArmatureObject armature, string sourceBonePath)
            {
                this.armature = armature;
                this.sourceBonePath = MeshNodePath.NormalizeOptional(sourceBonePath);
            }

            public bool Equals(SourceKey other)
            {
                return ReferenceEquals(armature, other.armature) && sourceBonePath == other.sourceBonePath;
            }

            public override bool Equals(object obj)
            {
                return obj is SourceKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((armature != null ? armature.GetInstanceID() : 0) * 397) ^
                           (sourceBonePath != null ? sourceBonePath.GetHashCode() : 0);
                }
            }
        }

        public bool SyncActiveBlendShapeWeights()
        {
            var oldWeights = (m_BlendShapeWeights ?? Enumerable.Empty<BlendShareProxyBlendShapeWeight>())
                .Where(weight => weight != null)
                .Select(weight => (weight.ShapeName, weight.Weight))
                .ToArray();
            var existingByName = (m_BlendShapeWeights ?? Enumerable.Empty<BlendShareProxyBlendShapeWeight>())
                .Where(weight => weight != null && !string.IsNullOrWhiteSpace(weight.ShapeName))
                .GroupBy(weight => weight.ShapeName)
                .ToDictionary(group => group.Key, group => group.First());

            m_BlendShapeWeights = GetActiveBlendShapeNames()
                .Select(shapeName =>
                {
                    if (existingByName.TryGetValue(shapeName, out var existing))
                    {
                        existing.Weight = Mathf.Clamp(existing.Weight, 0f, 100f);
                        return existing;
                    }

                    return new BlendShareProxyBlendShapeWeight(shapeName, GetDefaultBlendShapeWeight(shapeName));
                })
                .ToList();

            return oldWeights.Length != m_BlendShapeWeights.Count ||
                   oldWeights.Where((item, index) =>
                       item.ShapeName != m_BlendShapeWeights[index].ShapeName ||
                       !Mathf.Approximately(item.Weight, m_BlendShapeWeights[index].Weight)).Any();
        }

        public bool TryGetBlendShapeWeight(string shapeName, out float weight)
        {
            var entry = BlendShapeWeights.FirstOrDefault(item => item != null && item.ShapeName == shapeName);
            if (entry == null)
            {
                weight = 0f;
                return false;
            }

            weight = entry.Weight;
            return true;
        }

        private void OnValidate()
        {
            EnsureTargetRendererReferenceInitialized();
            if (!m_TargetRendererReference.IsConfigured)
            {
                var renderer = GetComponent<SkinnedMeshRenderer>();
                if (renderer != null)
                {
                    m_TargetRendererReference.Set(renderer);
                }
            }

            m_RendererNodePath = MeshNodePath.Normalize(m_RendererNodePath);
            m_BoneProxiesBySourcePath = new Dictionary<string, BlendShareBoneProxy>(StringComparer.Ordinal);
            SyncActiveBlendShapeWeights();
        }

        private AvatarObjectReference<SkinnedMeshRenderer> EnsureTargetRendererReferenceInitialized()
        {
            if (m_TargetRendererReference == null)
            {
                m_TargetRendererReference = new AvatarObjectReference<SkinnedMeshRenderer>();
            }

            return m_TargetRendererReference;
        }

        private IEnumerable<string> GetActiveBlendShapeNames()
        {
            return m_MeshData?.GetFeature<BlendShapeFeatureObject>()?.GetActiveBlendShapes()
                .Where(blendShape => blendShape != null && !string.IsNullOrWhiteSpace(blendShape.m_Name))
                .Select(blendShape => blendShape.m_Name)
                .Distinct() ?? Enumerable.Empty<string>();
        }

        private float GetDefaultBlendShapeWeight(string shapeName)
        {
            var renderer = TargetRenderer;
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (renderer == null || mesh == null || string.IsNullOrWhiteSpace(shapeName))
            {
                return 0f;
            }

            int index = mesh.GetBlendShapeIndex(shapeName);
            return index >= 0 ? renderer.GetBlendShapeWeight(index) : 0f;
        }
    }
}
