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
    public sealed class BlendShareBindPoseOverride
    {
        [SerializeField, NotKeyable]
        private string m_SourceBonePath;

        [SerializeField, NotKeyable]
        private Matrix4x4 m_BindPose = Matrix4x4.identity;

        /// <summary>Gets the normalized source bone path.</summary>
        public string SourceBonePath
        {
            get => MeshNodePath.NormalizeOptional(m_SourceBonePath);
            set => m_SourceBonePath = MeshNodePath.NormalizeOptional(value);
        }

        /// <summary>Gets or sets the renderer-relative bind-pose matrix.</summary>
        public Matrix4x4 BindPose
        {
            get => m_BindPose;
            set => m_BindPose = value;
        }

        /// <summary>Creates an empty override for Unity serialization.</summary>
        public BlendShareBindPoseOverride() { }

        /// <summary>Creates a source-path bind-pose override.</summary>
        public BlendShareBindPoseOverride(string sourceBonePath, Matrix4x4 bindPose)
        {
            SourceBonePath = sourceBonePath;
            BindPose = bindPose;
        }
    }

    public readonly struct BlendShareResolvedBone
    {
        /// <summary>Creates an exact resolved proxy binding.</summary>
        public BlendShareResolvedBone(
            BlendShareBoneProxy proxy,
            BlendShareBoneProxyBinding binding)
        {
            Proxy = proxy;
            Binding = binding;
            Transform = binding?.Transform;
        }

        /// <summary>Creates a resolved existing renderer bone.</summary>
        public BlendShareResolvedBone(Transform transform)
        {
            Proxy = null;
            Binding = null;
            Transform = transform;
        }

        /// <summary>Gets the owning proxy component, or null for an existing renderer bone.</summary>
        public BlendShareBoneProxy Proxy { get; }
        /// <summary>Gets the selected proxy binding, or null for an existing renderer bone.</summary>
        public BlendShareBoneProxyBinding Binding { get; }
        /// <summary>Gets the actual resolved bone Transform.</summary>
        public Transform Transform { get; }
        /// <summary>Gets whether an actual bone Transform was resolved.</summary>
        public bool IsResolved => Transform != null;
        /// <summary>Gets whether this resolution came from a proxy binding.</summary>
        public bool IsProxy => Proxy != null && Binding?.IsConfigured == true;
    }

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

        [SerializeField, NotKeyable]
        private List<BlendShareBindPoseOverride> m_BindPoseOverrides = new();

        [NonSerialized]
        private Dictionary<string, BlendShareResolvedBone> m_BonesBySourcePath = new(StringComparer.Ordinal);

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

        /// <summary>Gets the persistent renderer-relative bind-pose overrides.</summary>
        public IReadOnlyList<BlendShareBindPoseOverride> BindPoseOverrides =>
            m_BindPoseOverrides ??= new List<BlendShareBindPoseOverride>();

        /// <summary>Gets the currently cached proxy components.</summary>
        public IReadOnlyCollection<BlendShareBoneProxy> CachedBoneProxies =>
            EnsureBoneCache().Values
                .Select(resolved => resolved.Proxy)
                .Where(proxy => proxy != null)
                .Distinct()
                .ToArray();

        /// <summary>Gets the exact cached proxy binding selected for a required source bone path.</summary>
        public bool TryGetCachedBone(string sourceBonePath, out BlendShareResolvedBone resolved)
        {
            resolved = default;
            string normalizedPath = MeshNodePath.NormalizeOptional(sourceBonePath);
            return !string.IsNullOrEmpty(normalizedPath) &&
                   EnsureBoneCache().TryGetValue(normalizedPath, out resolved) &&
                   resolved.IsResolved;
        }

        /// <summary>Gets a stored bind-pose override for a source bone path.</summary>
        public bool TryGetBindPoseOverride(string sourceBonePath, out Matrix4x4 bindPose)
        {
            string normalizedPath = MeshNodePath.NormalizeOptional(sourceBonePath);
            var entry = BindPoseOverrides.LastOrDefault(candidate =>
                candidate != null && candidate.SourceBonePath == normalizedPath);
            bindPose = entry?.BindPose ?? Matrix4x4.identity;
            return entry != null;
        }

        /// <summary>Captures the selected proxy bone's current position as a persistent bind pose.</summary>
        public bool CaptureBindPose(string sourceBonePath)
        {
            if (TargetRenderer == null ||
                !TryGetCachedBone(sourceBonePath, out var resolved) ||
                resolved.Transform == null)
            {
                return false;
            }

            SetBindPoseOverride(
                sourceBonePath,
                resolved.Transform.worldToLocalMatrix * TargetRenderer.transform.localToWorldMatrix);
            return true;
        }

        /// <summary>Creates or replaces a persistent bind-pose override.</summary>
        public void SetBindPoseOverride(string sourceBonePath, Matrix4x4 bindPose)
        {
            string normalizedPath = MeshNodePath.NormalizeOptional(sourceBonePath);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return;
            }

            m_BindPoseOverrides ??= new List<BlendShareBindPoseOverride>();
            m_BindPoseOverrides.RemoveAll(candidate =>
                candidate == null || candidate.SourceBonePath == normalizedPath);
            m_BindPoseOverrides.Add(new BlendShareBindPoseOverride(normalizedPath, bindPose));
        }

        /// <summary>Removes a persistent bind-pose override so source bind-pose data is used.</summary>
        public bool RemoveBindPoseOverride(string sourceBonePath)
        {
            string normalizedPath = MeshNodePath.NormalizeOptional(sourceBonePath);
            return m_BindPoseOverrides != null &&
                   m_BindPoseOverrides.RemoveAll(candidate =>
                       candidate == null || candidate.SourceBonePath == normalizedPath) > 0;
        }

        private void SetBoneCache(
            IEnumerable<KeyValuePair<string, BlendShareResolvedBone>> references)
        {
            m_BonesBySourcePath = references?
                .Where(reference =>
                    !string.IsNullOrEmpty(MeshNodePath.NormalizeOptional(reference.Key)) &&
                    reference.Value.IsResolved)
                .GroupBy(reference => MeshNodePath.NormalizeOptional(reference.Key), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal) ??
                new Dictionary<string, BlendShareResolvedBone>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Refreshes transient proxy caches for the supplied meshes from one hierarchy-ordered proxy pass.
        /// </summary>
        public static void RefreshBoneProxyCaches(
            IEnumerable<BlendShareMesh> meshes,
            Transform targetRoot,
            IEnumerable<BlendShareBoneProxy> availableProxies)
        {
            var proxiesBySource = new Dictionary<SourceKey, BlendShareResolvedBone>();
            foreach (var proxy in (availableProxies ?? Enumerable.Empty<BlendShareBoneProxy>())
                         .Where(proxy => proxy != null && proxy.isActiveAndEnabled && proxy.SourceArmature != null)
                         .Distinct()
                         .OrderBy(proxy => GetHierarchyOrder(proxy.transform), StringComparer.Ordinal))
            {
                foreach (var binding in proxy.Bindings.Where(binding => binding?.IsConfigured == true))
                {
                    proxiesBySource[new SourceKey(proxy.SourceArmature, binding.SourceBonePath)] =
                        new BlendShareResolvedBone(proxy, binding);
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
            IReadOnlyDictionary<SourceKey, BlendShareResolvedBone> proxiesBySource)
        {
            var skin = MeshData?.GetFeature<SkinWeightFeatureObject>();
            var renderer = TargetRenderer;
            if (targetRoot == null || renderer == null || skin?.Armature == null)
            {
                SetBoneCache(Array.Empty<KeyValuePair<string, BlendShareResolvedBone>>());
                return;
            }

            var existingBonesByPath = (renderer.bones ?? Array.Empty<Transform>())
                .Where(bone => bone != null)
                .GroupBy(bone => MeshNodePath.Normalize(MeshNodePath.GetRelativePath(bone, targetRoot)), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            var references = new List<KeyValuePair<string, BlendShareResolvedBone>>();
            foreach (string sourceBonePath in skin.GetNeededBonePathsInArmatureOrder())
            {
                if (existingBonesByPath.TryGetValue(sourceBonePath, out var existingBone))
                {
                    references.Add(new KeyValuePair<string, BlendShareResolvedBone>(
                        sourceBonePath,
                        new BlendShareResolvedBone(existingBone)));
                }
                else if (proxiesBySource != null &&
                    proxiesBySource.TryGetValue(new SourceKey(skin.Armature, sourceBonePath), out var resolved))
                {
                    references.Add(new KeyValuePair<string, BlendShareResolvedBone>(sourceBonePath, resolved));
                }
            }

            SetBoneCache(references);
        }

        private Dictionary<string, BlendShareResolvedBone> EnsureBoneCache()
        {
            return m_BonesBySourcePath ??= new Dictionary<string, BlendShareResolvedBone>(StringComparer.Ordinal);
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
            m_BonesBySourcePath = new Dictionary<string, BlendShareResolvedBone>(StringComparer.Ordinal);
            m_BindPoseOverrides = (m_BindPoseOverrides ?? new List<BlendShareBindPoseOverride>())
                .Where(entry => entry != null && !string.IsNullOrEmpty(entry.SourceBonePath))
                .GroupBy(entry => entry.SourceBonePath, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToList();
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
