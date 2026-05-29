using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BoneGraph;
using Triturbo.BlendShare.Features.BlendShapes;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Scripting.APIUpdating;

namespace Triturbo.BlendShare.Components
{
    [Serializable]
    public sealed class BlendShareBoneProxyBinding
    {
        [SerializeField, NotKeyable]
        private string m_SourceBonePath;

        [SerializeField, NotKeyable]
        private BoneGraphObject m_BoneGraph;

        [SerializeField, NotKeyable]
        private BlendShareBoneProxy m_Proxy;

        public BoneGraphObject BoneGraph
        {
            get => m_BoneGraph;
            set => m_BoneGraph = value;
        }

        public string SourceBonePath
        {
            get => MeshNodePath.Normalize(m_SourceBonePath);
            set => m_SourceBonePath = MeshNodePath.Normalize(value);
        }

        public BlendShareBoneProxy Proxy
        {
            get => m_Proxy;
            set => m_Proxy = value;
        }

        public bool Matches(BoneGraphObject boneGraph, string sourceBonePath)
        {
            return m_BoneGraph == boneGraph &&
                   SourceBonePath == MeshNodePath.Normalize(sourceBonePath);
        }

        public void Sanitize()
        {
            m_SourceBonePath = MeshNodePath.Normalize(m_SourceBonePath);
        }
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
        private BlendShareCore m_Owner;

        [SerializeField, NotKeyable]
        private AvatarObjectReference<SkinnedMeshRenderer> m_TargetRendererReference = new();

        [SerializeField, NotKeyable]
        private MeshDataObject m_MeshData;

        [SerializeField, NotKeyable]
        private string m_RendererNodePath;

        [SerializeField]
        private bool m_EnabledForBuild = true;

        [SerializeField, NotKeyable]
        private string m_DiagnosticMessage;

        [SerializeField, NotKeyable]
        private List<BlendShareBoneProxyBinding> m_BoneProxyBindings = new();

        [SerializeField]
        private List<BlendShareProxyBlendShapeWeight> m_BlendShapeWeights = new();

        [NonSerialized]
        private UnityVertexMappingObject[] m_GenerationMappingOverrides = Array.Empty<UnityVertexMappingObject>();

        public BlendShareCore Owner
        {
            get => m_Owner;
            set => m_Owner = value;
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

        public string RendererNodePath
        {
            get => MeshNodePath.Normalize(m_RendererNodePath);
            set => m_RendererNodePath = MeshNodePath.Normalize(value);
        }

        public bool EnabledForBuild
        {
            get => m_EnabledForBuild;
            set => m_EnabledForBuild = value;
        }

        public string DiagnosticMessage
        {
            get => m_DiagnosticMessage;
            set => m_DiagnosticMessage = value;
        }

        public IReadOnlyList<BlendShareBoneProxyBinding> BoneProxyBindings =>
            m_BoneProxyBindings ??= new List<BlendShareBoneProxyBinding>();

        public IReadOnlyList<BlendShareProxyBlendShapeWeight> BlendShapeWeights =>
            m_BlendShapeWeights ??= new List<BlendShareProxyBlendShapeWeight>();

        public IReadOnlyList<UnityVertexMappingObject> GenerationMappingOverrides =>
            m_GenerationMappingOverrides ?? Array.Empty<UnityVertexMappingObject>();

        public void SetBoneProxyBindings(IEnumerable<BlendShareBoneProxyBinding> bindings)
        {
            m_BoneProxyBindings = (bindings ?? Enumerable.Empty<BlendShareBoneProxyBinding>())
                .Where(binding => binding != null && binding.BoneGraph != null && !string.IsNullOrWhiteSpace(binding.SourceBonePath))
                .Select(binding =>
                {
                    binding.Sanitize();
                    return binding;
                })
                .GroupBy(binding => $"{binding.BoneGraph.GetInstanceID()}:{binding.SourceBonePath}")
                .Select(group => group.First())
                .ToList();
        }

        public void SetGenerationMappingOverrides(IEnumerable<UnityVertexMappingObject> mappings)
        {
            m_GenerationMappingOverrides = (mappings ?? Enumerable.Empty<UnityVertexMappingObject>())
                .Where(mapping => mapping != null)
                .Distinct()
                .ToArray();
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
                        existing.Weight = existing.Weight;
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

        public bool TryGetBoneProxyBinding(
            BoneGraphObject boneGraph,
            string sourceBonePath,
            out BlendShareBoneProxyBinding binding)
        {
            string normalizedPath = MeshNodePath.Normalize(sourceBonePath);
            binding = BoneProxyBindings.FirstOrDefault(item =>
                item != null &&
                item.BoneGraph == boneGraph &&
                item.SourceBonePath == normalizedPath);
            return binding != null;
        }

        private void OnValidate()
        {
            if (m_Owner == null)
            {
                m_Owner = GetComponentInParent<BlendShareCore>(true);
            }

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
            m_DiagnosticMessage ??= string.Empty;
            SetBoneProxyBindings(m_BoneProxyBindings);
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
