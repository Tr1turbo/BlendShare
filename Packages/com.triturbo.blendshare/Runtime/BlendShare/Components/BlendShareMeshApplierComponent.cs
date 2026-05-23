using Triturbo.BlendShare.Core;
using UnityEngine;
using UnityEngine.Animations;

namespace Triturbo.BlendShare.Components
{
    [DisallowMultipleComponent]
    [AddComponentMenu("BlendShare/BlendShare Mesh Applier")]
    public sealed class BlendShareMeshApplierComponent : MonoBehaviour
    {
        [SerializeField, NotKeyable]
        private BlendShareApplierComponent m_Owner;

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

        public BlendShareApplierComponent Owner
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

        public void SetDiagnostic(bool isStale, string message)
        {
            m_IsStale = isStale;
            m_DiagnosticMessage = message;
        }

        private void OnValidate()
        {
            if (m_Owner == null)
            {
                m_Owner = GetComponentInParent<BlendShareApplierComponent>(true);
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
