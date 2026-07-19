using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BlendShapes;
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
