using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Assets;
using UnityEngine;
using UnityEngine.Animations;
#if BS_HAS_VRCSDK3_BASE
using VRC.SDKBase;
#endif

namespace Triturbo.BlendShare.Components
{
    [Serializable]
    public sealed class BlendShareBindPoseOverride
    {
        [SerializeField, NotKeyable]
        private string m_SourceBonePath = string.Empty;

        [SerializeField, NotKeyable]
        private Matrix4x4 m_BindPose = Matrix4x4.identity;

        public string SourceBonePath
        {
            get => RuntimeNodePathUtility.Normalize(m_SourceBonePath);
            set => m_SourceBonePath = RuntimeNodePathUtility.Normalize(value);
        }

        public Matrix4x4 BindPose
        {
            get => m_BindPose;
            set => m_BindPose = value;
        }

        public BlendShareBindPoseOverride()
        {
        }

        public BlendShareBindPoseOverride(string sourceBonePath, Matrix4x4 bindPose)
        {
            SourceBonePath = sourceBonePath;
            BindPose = bindPose;
        }
    }

    public readonly struct BlendShareResolvedBone
    {
        public BlendShareResolvedBone(
            BlendShareBoneProxy proxy,
            BlendShareBoneProxyBinding binding)
        {
            Proxy = proxy;
            Binding = binding;
            Transform = binding?.Transform;
        }

        public BlendShareResolvedBone(Transform transform)
        {
            Proxy = null;
            Binding = null;
            Transform = transform;
        }

        public BlendShareBoneProxy Proxy { get; }
        public BlendShareBoneProxyBinding Binding { get; }
        public Transform Transform { get; }
        public bool IsResolved => Transform != null;
        public bool IsProxy => Proxy != null && Binding?.IsConfigured == true;
    }

    [Serializable]
    public sealed class BlendShareProxyBlendShapeWeight
    {
        public const string ShapeNameFieldName = "m_ShapeName";
        public const string WeightFieldName = "m_Weight";

        [SerializeField, NotKeyable]
        private string m_ShapeName = string.Empty;

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

        public BlendShareProxyBlendShapeWeight()
        {
        }

        public BlendShareProxyBlendShapeWeight(string shapeName, float weight)
        {
            ShapeName = shapeName;
            Weight = weight;
        }
    }

    /// <summary>
    /// Canonical BlendShare 2.0 authoring component attaching a <see cref="BlendShareAsset"/> /
    /// <see cref="BlendShareMeshAsset"/> patch to a target <see cref="SkinnedMeshRenderer"/>.
    /// Zero Editor or FBX dependencies, and zero passive <c>OnValidate</c> scene dirtying.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("BlendShare/BlendShare Mesh")]
    public sealed class BlendShareMesh : BlendShareComponent
#if BS_HAS_VRCSDK3_BASE
        , IEditorOnly
#endif
    {
        public const string BlendShapeWeightsFieldName = "m_BlendShapeWeights";

        [SerializeField, NotKeyable]
        private BlendShareAsset m_Patch;

        [SerializeField, NotKeyable]
        private BlendShareMeshAsset m_MeshAsset;

        [SerializeField, NotKeyable]
        private string m_MeshId = string.Empty;

        [SerializeField, NotKeyable]
        private AvatarObjectReference<SkinnedMeshRenderer> m_TargetRendererReference = new AvatarObjectReference<SkinnedMeshRenderer>();

        [SerializeField, NotKeyable]
        private GameObject m_TargetFbxOverride;

        [SerializeField, NotKeyable]
        private Mesh m_ReferenceMesh;

        [SerializeField, NotKeyable]
        private bool m_CreateMissingBones = true;

        [SerializeField, NotKeyable]
        private string m_RendererNodePath = string.Empty;

        [SerializeField]
        private List<BlendShareProxyBlendShapeWeight> m_BlendShapeWeights = new List<BlendShareProxyBlendShapeWeight>();

        [SerializeField, NotKeyable]
        private List<BlendShareBindPoseOverride> m_BindPoseOverrides = new List<BlendShareBindPoseOverride>();

        [NonSerialized]
        private Dictionary<string, BlendShareResolvedBone> m_BonesBySourcePath =
            new Dictionary<string, BlendShareResolvedBone>(StringComparer.Ordinal);

        public BlendShareAsset Patch
        {
            get => m_Patch;
            set => m_Patch = value;
        }

        public BlendShareMeshAsset MeshAsset
        {
            get => m_MeshAsset;
            set => m_MeshAsset = value;
        }

        public string MeshId
        {
            get => m_MeshId ?? string.Empty;
            set => m_MeshId = value ?? string.Empty;
        }

        public BlendShareAsset ResolvedPatch =>
            m_Patch != null ? m_Patch : (m_MeshAsset != null ? m_MeshAsset.Root : null);

        public string ResolvedMeshId =>
            m_MeshAsset != null && !string.IsNullOrEmpty(m_MeshAsset.MeshId)
                ? m_MeshAsset.MeshId
                : (m_MeshId ?? string.Empty);

        public SkinnedMeshRenderer TargetRenderer
        {
            get => m_TargetRendererReference != null && m_TargetRendererReference.IsConfigured
                ? m_TargetRendererReference.Get(this)
                : null;
            set
            {
                if (m_TargetRendererReference == null)
                {
                    m_TargetRendererReference = new AvatarObjectReference<SkinnedMeshRenderer>();
                }

                m_TargetRendererReference.Set(value);
            }
        }

        public AvatarObjectReference<SkinnedMeshRenderer> TargetRendererReference => m_TargetRendererReference;

        public GameObject TargetFbxOverride
        {
            get => m_TargetFbxOverride;
            set => m_TargetFbxOverride = value;
        }

        public Mesh ReferenceMesh
        {
            get => m_ReferenceMesh;
            set => m_ReferenceMesh = value;
        }

        public bool CreateMissingBones
        {
            get => m_CreateMissingBones;
            set => m_CreateMissingBones = value;
        }

        public string RendererNodePath
        {
            get => RuntimeNodePathUtility.Normalize(m_RendererNodePath);
            set => m_RendererNodePath = RuntimeNodePathUtility.Normalize(value);
        }

        public IReadOnlyList<BlendShareProxyBlendShapeWeight> BlendShapeWeights =>
            m_BlendShapeWeights ?? (m_BlendShapeWeights = new List<BlendShareProxyBlendShapeWeight>());

        public IReadOnlyList<BlendShareBindPoseOverride> BindPoseOverrides =>
            m_BindPoseOverrides ?? (m_BindPoseOverrides = new List<BlendShareBindPoseOverride>());

        public IReadOnlyCollection<BlendShareBoneProxy> CachedBoneProxies =>
            EnsureBoneCache().Values
                .Select(resolved => resolved.Proxy)
                .Where(proxy => proxy != null)
                .Distinct()
                .ToArray();

        public bool TryGetCachedBone(string sourceBonePath, out BlendShareResolvedBone resolved)
        {
            resolved = default;
            string normalizedPath = RuntimeNodePathUtility.Normalize(sourceBonePath);
            return !string.IsNullOrEmpty(normalizedPath) &&
                   EnsureBoneCache().TryGetValue(normalizedPath, out resolved) &&
                   resolved.IsResolved;
        }

        public void SetBoneCache(IEnumerable<KeyValuePair<string, BlendShareResolvedBone>> references)
        {
            m_BonesBySourcePath = (references ?? Enumerable.Empty<KeyValuePair<string, BlendShareResolvedBone>>())
                .Where(reference =>
                    !string.IsNullOrEmpty(RuntimeNodePathUtility.Normalize(reference.Key)) &&
                    reference.Value.IsResolved)
                .GroupBy(reference => RuntimeNodePathUtility.Normalize(reference.Key), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        }

        public bool TryGetBindPoseOverride(string sourceBonePath, out Matrix4x4 bindPose)
        {
            string normalizedPath = RuntimeNodePathUtility.Normalize(sourceBonePath);
            var entry = BindPoseOverrides.LastOrDefault(candidate =>
                candidate != null && candidate.SourceBonePath == normalizedPath);
            bindPose = entry?.BindPose ?? Matrix4x4.identity;
            return entry != null;
        }

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

        public void SetBindPoseOverride(string sourceBonePath, Matrix4x4 bindPose)
        {
            string normalizedPath = RuntimeNodePathUtility.Normalize(sourceBonePath);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return;
            }

            m_BindPoseOverrides ??= new List<BlendShareBindPoseOverride>();
            m_BindPoseOverrides.RemoveAll(candidate =>
                candidate == null || candidate.SourceBonePath == normalizedPath);
            m_BindPoseOverrides.Add(new BlendShareBindPoseOverride(normalizedPath, bindPose));
        }

        public bool RemoveBindPoseOverride(string sourceBonePath)
        {
            string normalizedPath = RuntimeNodePathUtility.Normalize(sourceBonePath);
            return m_BindPoseOverrides != null &&
                   m_BindPoseOverrides.RemoveAll(candidate =>
                       candidate == null || candidate.SourceBonePath == normalizedPath) > 0;
        }

        public void SetBlendShapeWeight(string shapeName, float weight)
        {
            if (string.IsNullOrWhiteSpace(shapeName))
            {
                return;
            }

            m_BlendShapeWeights ??= new List<BlendShareProxyBlendShapeWeight>();
            for (int i = 0; i < m_BlendShapeWeights.Count; i++)
            {
                if (m_BlendShapeWeights[i] != null &&
                    string.Equals(m_BlendShapeWeights[i].ShapeName, shapeName, StringComparison.Ordinal))
                {
                    m_BlendShapeWeights[i].Weight = weight;
                    return;
                }
            }

            m_BlendShapeWeights.Add(new BlendShareProxyBlendShapeWeight(shapeName, weight));
        }

        public void ClearBlendShapeWeights()
        {
            m_BlendShapeWeights?.Clear();
        }

        /// <summary>
        /// Explicitly synchronizes serialized blendshape weight entries against the active shape names
        /// resolved by Editor services. Never invoked passively from <c>OnValidate</c>.
        /// </summary>
        public bool SyncActiveBlendShapeWeights(IEnumerable<string> activeShapeNames)
        {
            var oldWeights = (m_BlendShapeWeights ?? Enumerable.Empty<BlendShareProxyBlendShapeWeight>())
                .Where(weight => weight != null)
                .Select(weight => (weight.ShapeName, weight.Weight))
                .ToArray();

            var existingByName = new Dictionary<string, BlendShareProxyBlendShapeWeight>(StringComparer.Ordinal);
            if (m_BlendShapeWeights != null)
            {
                for (int i = 0; i < m_BlendShapeWeights.Count; i++)
                {
                    var item = m_BlendShapeWeights[i];
                    if (item != null && !string.IsNullOrWhiteSpace(item.ShapeName) && !existingByName.ContainsKey(item.ShapeName))
                    {
                        existingByName.Add(item.ShapeName, item);
                    }
                }
            }

            var next = new List<BlendShareProxyBlendShapeWeight>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string shapeName in activeShapeNames ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(shapeName) || !seen.Add(shapeName))
                {
                    continue;
                }

                if (existingByName.TryGetValue(shapeName, out var existing))
                {
                    existing.Weight = Mathf.Clamp(existing.Weight, 0f, 100f);
                    next.Add(existing);
                }
                else
                {
                    next.Add(new BlendShareProxyBlendShapeWeight(shapeName, GetDefaultBlendShapeWeight(shapeName)));
                }
            }

            m_BlendShapeWeights = next;
            if (oldWeights.Length != m_BlendShapeWeights.Count)
            {
                return true;
            }

            for (int i = 0; i < oldWeights.Length; i++)
            {
                if (!string.Equals(oldWeights[i].ShapeName, m_BlendShapeWeights[i].ShapeName, StringComparison.Ordinal) ||
                    !Mathf.Approximately(oldWeights[i].Weight, m_BlendShapeWeights[i].Weight))
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryGetBlendShapeWeight(string shapeName, out float weight)
        {
            var weights = BlendShapeWeights;
            for (int i = 0; i < weights.Count; i++)
            {
                var item = weights[i];
                if (item != null && string.Equals(item.ShapeName, shapeName, StringComparison.Ordinal))
                {
                    weight = item.Weight;
                    return true;
                }
            }

            weight = 0f;
            return false;
        }

        private Dictionary<string, BlendShareResolvedBone> EnsureBoneCache()
        {
            return m_BonesBySourcePath ??= new Dictionary<string, BlendShareResolvedBone>(StringComparer.Ordinal);
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
