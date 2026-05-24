using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BoneGraph;
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
        private BlendShareBoneProxyComponent m_Proxy;

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

        public BlendShareBoneProxyComponent Proxy
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

    [DisallowMultipleComponent]
    [AddComponentMenu("BlendShare/BlendShare Mesh")]
    [MovedFrom(true, null, null, "BlendShareMeshApplierComponent")]
    public sealed class BlendShareMeshComponent : MonoBehaviour
    {
        [SerializeField, NotKeyable]
        private BlendShareComponent m_Owner;

        [SerializeField, NotKeyable]
        private AvatarObjectReference<SkinnedMeshRenderer> m_TargetRendererReference = new();

        [SerializeField, NotKeyable]
        private MeshDataObject m_MeshData;

        [SerializeField, NotKeyable]
        private string m_RendererNodePath;

        [SerializeField, NotKeyable]
        private bool m_EnabledForBuild = true;

        [SerializeField, NotKeyable]
        private bool m_IsStale;

        [SerializeField, NotKeyable]
        private string m_DiagnosticMessage;

        [SerializeField, NotKeyable]
        private List<BlendShareBoneProxyBinding> m_BoneProxyBindings = new();

        public BlendShareComponent Owner
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

        public bool IsStale
        {
            get => m_IsStale;
            set => m_IsStale = value;
        }

        public string DiagnosticMessage
        {
            get => m_DiagnosticMessage;
            set => m_DiagnosticMessage = value;
        }

        public IReadOnlyList<BlendShareBoneProxyBinding> BoneProxyBindings =>
            m_BoneProxyBindings ??= new List<BlendShareBoneProxyBinding>();

        public void SetDiagnostic(bool isStale, string message)
        {
            m_IsStale = isStale;
            m_DiagnosticMessage = message;
        }

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
                m_Owner = GetComponentInParent<BlendShareComponent>(true);
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
        }

        private AvatarObjectReference<SkinnedMeshRenderer> EnsureTargetRendererReferenceInitialized()
        {
            if (m_TargetRendererReference == null)
            {
                m_TargetRendererReference = new AvatarObjectReference<SkinnedMeshRenderer>();
            }

            return m_TargetRendererReference;
        }
    }
}
